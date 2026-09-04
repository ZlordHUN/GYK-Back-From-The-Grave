using System;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Advances an observed animation between packets without predicting a native
    /// exit-time transition. Fishing clips can exit at a loop boundary even when
    /// Unity marks the clip as looping.
    /// </summary>
    internal static class FishingAnimationPlayback
    {
        private const double MaxProjectionSeconds = 0.15;
        private const double BoundaryMargin = 0.0001;
        private const double FloatRelativePrecision = 1.1920928955078125e-7;

        internal static float ProjectNormalizedTime(
            float normalizedTime, float length, float speed, double elapsedSeconds)
        {
            if (!IsFinite(normalizedTime))
                return 0f;
            if (!IsFinite(length) || !IsFinite(speed) ||
                length <= 0.001f || speed <= 0f ||
                double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) ||
                elapsedSeconds <= 0.0)
                return normalizedTime;

            double elapsed = Math.Min(elapsedSeconds, MaxProjectionSeconds);
            double projected = normalizedTime + elapsed * speed / length;
            double nextBoundary = Math.Floor((double)normalizedTime) + 1.0;

            // A fixed margin can round back to the integer at large loop counts.
            // Retain at least float-relative precision before converting to float.
            double margin = Math.Max(BoundaryMargin,
                Math.Abs(nextBoundary) * FloatRelativePrecision);
            float bounded = (float)Math.Min(projected, nextBoundary - margin);

            // A newly received owner pose may already be closer to the boundary
            // than our prediction margin, or may have entered a later loop.
            return Math.Max(normalizedTime, bounded);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}


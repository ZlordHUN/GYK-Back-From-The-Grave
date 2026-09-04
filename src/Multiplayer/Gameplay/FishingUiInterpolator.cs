using System;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Renders received minigame samples on a short delayed timeline, filling the
    /// frames between network updates without predicting fishing outcomes.
    /// The caller resets this history at each Pulling phase/session boundary.
    /// </summary>
    internal sealed class FishingUiInterpolator
    {
        private const int Capacity = 16;
        private const double PlaybackDelay = 0.100;
        private const double MaximumSampleGap = 0.250;

        private struct Sample
        {
            public double ReceivedAt;
            public FishingUiFrame Frame;
        }

        private readonly Sample[] samples = new Sample[Capacity];
        private int start;
        private int count;

        public void Push(double receivedAt, FishingUiFrame frame)
        {
            if (!IsFinite(receivedAt) || !IsFinite(frame.RodPosition) ||
                !IsFinite(frame.FishPosition) || !IsFinite(frame.RodSize) ||
                !IsFinite(frame.Progress))
                return;

            var sample = new Sample { ReceivedAt = receivedAt, Frame = frame };
            if (count != 0)
            {
                int latestIndex = (start + count - 1) % Capacity;
                double previousTime = samples[latestIndex].ReceivedAt;
                if (receivedAt < previousTime)
                    return;
                if (receivedAt == previousTime)
                {
                    // Several packets can be drained in the same receiver frame.
                    // Keep the newest accepted value without a zero-length segment.
                    samples[latestIndex] = sample;
                    return;
                }
                if (receivedAt - previousTime > MaximumSampleGap)
                    Reset();
            }

            if (count == Capacity)
            {
                samples[start] = sample;
                start = (start + 1) % Capacity;
            }
            else
            {
                samples[(start + count) % Capacity] = sample;
                count++;
            }
        }

        public bool TrySample(double now, out FishingUiFrame frame)
        {
            frame = default(FishingUiFrame);
            if (count == 0 || !IsFinite(now))
                return false;

            double playbackTime = now - PlaybackDelay;
            Sample previous = samples[start];
            if (playbackTime <= previous.ReceivedAt)
            {
                // A new catch is visible immediately, even before a second packet.
                frame = previous.Frame;
                return true;
            }

            for (int i = 1; i < count; i++)
            {
                Sample next = samples[(start + i) % Capacity];
                if (playbackTime <= next.ReceivedAt)
                {
                    double fraction = (playbackTime - previous.ReceivedAt) /
                        (next.ReceivedAt - previous.ReceivedAt);
                    frame = Interpolate(previous.Frame, next.Frame, fraction);
                    return true;
                }
                previous = next;
            }

            // Packet loss/stalls freeze at the latest observation. Do not invent
            // fish movement, increasing progress, or a success/failure outcome.
            frame = previous.Frame;
            return true;
        }

        public void Reset()
        {
            start = 0;
            count = 0;
        }

        private static FishingUiFrame Interpolate(FishingUiFrame a, FishingUiFrame b, double fraction)
        {
            if (fraction <= 0.0)
                return a;
            if (fraction >= 1.0)
                return b;

            return new FishingUiFrame
            {
                RodPosition = Lerp(a.RodPosition, b.RodPosition, fraction),
                FishPosition = Lerp(a.FishPosition, b.FishPosition, fraction),
                RodSize = Lerp(a.RodSize, b.RodSize, fraction),
                // Fishing progress may rise or fall; both directions are real.
                Progress = Lerp(a.Progress, b.Progress, fraction)
            };
        }

        private static float Lerp(float a, float b, double fraction) =>
            (float)(a + ((double)b - a) * fraction);

        private static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }
}

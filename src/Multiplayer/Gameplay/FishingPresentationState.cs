using System;
using System.IO;
using System.Text;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Presentation owned by the sending player. This contains no fishing simulation
    /// or inventory state, so observers cannot change the catch or consume bait.
    /// </summary>
    internal sealed class FishingPresentationState
    {
        public uint Sequence;
        public bool Active;
        public byte Phase;
        public bool FacingRight;
        public byte Distance;
        public bool CanTakeOut;
        public bool Success;
        public string FishItemId = string.Empty;
        public float RodPosition;
        public float FishPosition;
        public float RodSize;
        public float Progress;
        public int AnimationHash;
        public float AnimationTime;
        public float AnimationSpeed;
        public float AnimationLength;
    }

    internal static class FishingPresentationCodec
    {
        internal const byte PayloadVersion = 1;
        internal const int MaxPayloadBytes = 512;
        internal const int MaxFishItemChars = 128;
        internal const int MaxFishItemBytes = 384;
        private const int FixedPayloadBytes = 42;
        private const byte ActiveFlag = 1;
        private const byte FacingRightFlag = 2;
        private const byte CanTakeOutFlag = 4;
        private const byte SuccessFlag = 8;
        private const byte KnownFlags = ActiveFlag | FacingRightFlag | CanTakeOutFlag | SuccessFlag;
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>Returns null instead of emitting an invalid presentation.</summary>
        internal static byte[] Serialize(FishingPresentationState state)
        {
            if (!IsValid(state))
                return null;

            byte[] fishItemBytes;
            try
            {
                fishItemBytes = StrictUtf8.GetBytes(state.FishItemId);
            }
            catch (EncoderFallbackException)
            {
                return null;
            }
            if (fishItemBytes.Length > MaxFishItemBytes)
                return null;

            using (var stream = new MemoryStream(FixedPayloadBytes + fishItemBytes.Length))
            using (var writer = new BinaryWriter(stream, StrictUtf8))
            {
                writer.Write(PayloadVersion);
                writer.Write(state.Sequence);
                byte flags = (byte)((state.Active ? ActiveFlag : 0) |
                    (state.FacingRight ? FacingRightFlag : 0) |
                    (state.CanTakeOut ? CanTakeOutFlag : 0) |
                    (state.Success ? SuccessFlag : 0));
                writer.Write(flags);
                writer.Write(state.Phase);
                writer.Write(state.Distance);
                writer.Write(state.RodPosition);
                writer.Write(state.FishPosition);
                writer.Write(state.RodSize);
                writer.Write(state.Progress);
                writer.Write(state.AnimationHash);
                writer.Write(state.AnimationTime);
                writer.Write(state.AnimationSpeed);
                writer.Write(state.AnimationLength);
                writer.Write((ushort)fishItemBytes.Length);
                writer.Write(fishItemBytes);
                writer.Flush();
                return stream.ToArray();
            }
        }

        internal static bool TryDeserialize(byte[] payload, out FishingPresentationState state)
        {
            state = null;
            if (payload == null || payload.Length < FixedPayloadBytes ||
                payload.Length > MaxPayloadBytes)
                return false;

            try
            {
                using (var stream = new MemoryStream(payload, false))
                using (var reader = new BinaryReader(stream, StrictUtf8))
                {
                    if (reader.ReadByte() != PayloadVersion)
                        return false;

                    uint sequence = reader.ReadUInt32();
                    byte flags = reader.ReadByte();
                    if ((flags & ~KnownFlags) != 0)
                        return false;

                    var candidate = new FishingPresentationState
                    {
                        Sequence = sequence,
                        Active = (flags & ActiveFlag) != 0,
                        FacingRight = (flags & FacingRightFlag) != 0,
                        CanTakeOut = (flags & CanTakeOutFlag) != 0,
                        Success = (flags & SuccessFlag) != 0,
                        Phase = reader.ReadByte(),
                        Distance = reader.ReadByte(),
                        RodPosition = reader.ReadSingle(),
                        FishPosition = reader.ReadSingle(),
                        RodSize = reader.ReadSingle(),
                        Progress = reader.ReadSingle(),
                        AnimationHash = reader.ReadInt32(),
                        AnimationTime = reader.ReadSingle(),
                        AnimationSpeed = reader.ReadSingle(),
                        AnimationLength = reader.ReadSingle()
                    };
                    if (!IsValid(candidate))
                        return false;

                    int fishItemBytes = reader.ReadUInt16();
                    if (fishItemBytes > MaxFishItemBytes ||
                        stream.Length - stream.Position != fishItemBytes)
                        return false;

                    // Check the byte limit and remaining packet before allocating either
                    // the byte buffer or decoded string. Strict UTF-8 rejects malformed IDs.
                    candidate.FishItemId = StrictUtf8.GetString(reader.ReadBytes(fishItemBytes));
                    if (!IsValid(candidate) || stream.Position != stream.Length)
                        return false;

                    state = candidate;
                    return true;
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        private static bool IsValid(FishingPresentationState state)
        {
            return state != null &&
                (state.Active ? state.Phase >= 1 && state.Phase <= 6 : state.Phase == 0) &&
                state.Distance <= 3 &&
                state.FishItemId != null && state.FishItemId.Length <= MaxFishItemChars &&
                InRange(state.RodPosition, -1f, 2f) &&
                InRange(state.FishPosition, -1f, 2f) &&
                InRange(state.RodSize, 0f, 1f) &&
                InRange(state.Progress, 0f, 1f) &&
                InRange(state.AnimationTime, 0f, 1000000f) &&
                InRange(state.AnimationSpeed, 0f, 10f) &&
                InRange(state.AnimationLength, 0f, 120f);
        }

        private static bool InRange(float value, float min, float max)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) &&
                value >= min && value <= max;
        }
    }
}

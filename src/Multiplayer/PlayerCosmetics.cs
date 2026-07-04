using System;
using System.Collections.Generic;
using UnityEngine;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Represents player cosmetic customization options.
    /// Each option is an index into a color palette.
    /// </summary>
    [Serializable]
    public class PlayerCosmetics
    {
        // Color palette indices (0-7 for each option)
        public int PantsColor { get; set; } = 0;
        public int ShirtColor { get; set; } = 0;
        public int HairColor { get; set; } = 0;
        public int SkinTone { get; set; } = 0;
        public int EyeColor { get; set; } = 0;
        
        // Number of options per category
        public const int MAX_PANTS_OPTIONS = 8;
        public const int MAX_SHIRT_OPTIONS = 8;
        public const int MAX_HAIR_OPTIONS = 8;
        public const int MAX_SKIN_OPTIONS = 6;
        public const int MAX_EYE_OPTIONS = 8;
        
        // Predefined color palettes for simple tinting (when shader not available)
        public static readonly Color[] PantsColors = new Color[]
        {
            new Color(0.25f, 0.20f, 0.15f), // Brown (default)
            new Color(0.15f, 0.15f, 0.25f), // Dark Blue
            new Color(0.20f, 0.10f, 0.10f), // Dark Red
            new Color(0.10f, 0.20f, 0.10f), // Dark Green
            new Color(0.20f, 0.20f, 0.20f), // Gray
            new Color(0.10f, 0.10f, 0.10f), // Black
            new Color(0.30f, 0.25f, 0.15f), // Tan
            new Color(0.25f, 0.15f, 0.25f), // Purple
        };
        
        public static readonly Color[] ShirtColors = new Color[]
        {
            new Color(0.85f, 0.80f, 0.70f), // Beige (default)
            new Color(0.70f, 0.70f, 0.85f), // Light Blue
            new Color(0.85f, 0.70f, 0.70f), // Light Red
            new Color(0.70f, 0.85f, 0.70f), // Light Green
            new Color(0.95f, 0.95f, 0.95f), // White
            new Color(0.50f, 0.50f, 0.50f), // Gray
            new Color(0.85f, 0.85f, 0.60f), // Yellow
            new Color(0.85f, 0.70f, 0.85f), // Pink
        };
        
        public static readonly Color[] HairColors = new Color[]
        {
            new Color(0.35f, 0.25f, 0.15f), // Brown (default)
            new Color(0.95f, 0.85f, 0.60f), // Blonde
            new Color(0.10f, 0.10f, 0.10f), // Black
            new Color(0.60f, 0.30f, 0.15f), // Auburn
            new Color(0.50f, 0.50f, 0.55f), // Gray
            new Color(0.85f, 0.85f, 0.85f), // White
            new Color(0.70f, 0.35f, 0.25f), // Ginger
            new Color(0.20f, 0.15f, 0.10f), // Dark Brown
        };
        
        public static readonly Color[] SkinTones = new Color[]
        {
            new Color(0.95f, 0.85f, 0.75f), // Light (default)
            new Color(0.85f, 0.70f, 0.55f), // Medium Light
            new Color(0.75f, 0.55f, 0.40f), // Medium
            new Color(0.60f, 0.45f, 0.35f), // Medium Dark
            new Color(0.45f, 0.35f, 0.25f), // Dark
            new Color(0.35f, 0.25f, 0.20f), // Very Dark
        };
        
        public static readonly Color[] EyeColors = new Color[]
        {
            new Color(0.40f, 0.30f, 0.20f), // Brown (default)
            new Color(0.30f, 0.50f, 0.70f), // Blue
            new Color(0.35f, 0.55f, 0.35f), // Green
            new Color(0.55f, 0.45f, 0.35f), // Hazel
            new Color(0.30f, 0.30f, 0.30f), // Gray
            new Color(0.60f, 0.40f, 0.60f), // Violet
            new Color(0.50f, 0.35f, 0.20f), // Amber
            new Color(0.20f, 0.20f, 0.20f), // Black
        };
        
        /// <summary>
        /// Default cosmetics (vanilla player look)
        /// </summary>
        public static PlayerCosmetics Default => new PlayerCosmetics();
        
        /// <summary>
        /// Generate random cosmetics
        /// </summary>
        public static PlayerCosmetics Random()
        {
            return new PlayerCosmetics
            {
                PantsColor = UnityEngine.Random.Range(0, MAX_PANTS_OPTIONS),
                ShirtColor = UnityEngine.Random.Range(0, MAX_SHIRT_OPTIONS),
                HairColor = UnityEngine.Random.Range(0, MAX_HAIR_OPTIONS),
                SkinTone = UnityEngine.Random.Range(0, MAX_SKIN_OPTIONS),
                EyeColor = UnityEngine.Random.Range(0, MAX_EYE_OPTIONS),
            };
        }
        
        /// <summary>
        /// Generate cosmetics based on Steam ID (deterministic)
        /// </summary>
        public static PlayerCosmetics FromSteamID(ulong steamId)
        {
            // Use Steam ID to seed deterministic "random" selection
            int seed = (int)(steamId % int.MaxValue);
            var rng = new System.Random(seed);
            
            return new PlayerCosmetics
            {
                PantsColor = rng.Next(MAX_PANTS_OPTIONS),
                ShirtColor = rng.Next(MAX_SHIRT_OPTIONS),
                HairColor = rng.Next(MAX_HAIR_OPTIONS),
                SkinTone = rng.Next(MAX_SKIN_OPTIONS),
                EyeColor = rng.Next(MAX_EYE_OPTIONS),
            };
        }
        
        /// <summary>
        /// Preset cosmetics to differentiate Player 2 from Player 1
        /// </summary>
        public static PlayerCosmetics Player2Default => new PlayerCosmetics
        {
            PantsColor = 1, // Dark Blue
            ShirtColor = 2, // Light Red
            HairColor = 1, // Blonde
            SkinTone = 0,   // Light
            EyeColor = 1,   // Blue
        };
        
        /// <summary>
        /// Pack cosmetics into a single byte for efficient network transfer
        /// Format: PPPSSSHH (3 bits pants, 3 bits shirt, 2 bits hair MSB)
        ///         HHEESSSS (2 bits hair LSB, 2 bits eye, 4 bits skin+reserved)
        /// Simplified: Just use 5 bytes, one per option
        /// </summary>
        public byte[] ToBytes()
        {
            return new byte[]
            {
                (byte)PantsColor,
                (byte)ShirtColor,
                (byte)HairColor,
                (byte)SkinTone,
                (byte)EyeColor,
            };
        }
        
        /// <summary>
        /// Unpack cosmetics from bytes
        /// </summary>
        public static PlayerCosmetics FromBytes(byte[] data)
        {
            if (data == null || data.Length < 5)
                return Default;
                
            return new PlayerCosmetics
            {
                PantsColor = Mathf.Clamp(data[0], 0, MAX_PANTS_OPTIONS - 1),
                ShirtColor = Mathf.Clamp(data[1], 0, MAX_SHIRT_OPTIONS - 1),
                HairColor = Mathf.Clamp(data[2], 0, MAX_HAIR_OPTIONS - 1),
                SkinTone = Mathf.Clamp(data[3], 0, MAX_SKIN_OPTIONS - 1),
                EyeColor = Mathf.Clamp(data[4], 0, MAX_EYE_OPTIONS - 1),
            };
        }
        
        public override string ToString()
        {
            return $"Cosmetics(Pants={PantsColor}, Shirt={ShirtColor}, Hair={HairColor}, Skin={SkinTone}, Eyes={EyeColor})";
        }
        
        public bool Equals(PlayerCosmetics other)
        {
            if (other == null) return false;
            return PantsColor == other.PantsColor &&
                   ShirtColor == other.ShirtColor &&
                   HairColor == other.HairColor &&
                   SkinTone == other.SkinTone &&
                   EyeColor == other.EyeColor;
        }
    }
}

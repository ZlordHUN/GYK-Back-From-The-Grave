using System;
using System.Collections.Generic;
using UnityEngine;

namespace GraveyardKeeperCoop.Multiplayer
{
    public enum CosmeticCategory
    {
        Pants,
        Shirt,
        Hair,
        Eyes,
        Skin,
    }

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
        public int PantsTone { get; set; } = DEFAULT_TONE;
        public int ShirtTone { get; set; } = DEFAULT_TONE;
        public int HairTone { get; set; } = DEFAULT_TONE;
        public int SkinShade { get; set; } = DEFAULT_TONE;
        public int EyeTone { get; set; } = DEFAULT_TONE;
        
        // Number of options per category
        public const int MAX_PANTS_OPTIONS = 8;
        public const int MAX_SHIRT_OPTIONS = 8;
        public const int MAX_HAIR_OPTIONS = 8;
        public const int MAX_SKIN_OPTIONS = 6;
        public const int MAX_EYE_OPTIONS = 8;
        public const int MAX_TONE_OPTIONS = 8;
        public const int DEFAULT_TONE = 3;
        public const int MAX_SELECTION_OPTIONS = 64;
        
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

        public PlayerCosmetics Clone()
        {
            return new PlayerCosmetics
            {
                PantsColor = PantsColor,
                ShirtColor = ShirtColor,
                HairColor = HairColor,
                SkinTone = SkinTone,
                EyeColor = EyeColor,
                PantsTone = PantsTone,
                ShirtTone = ShirtTone,
                HairTone = HairTone,
                SkinShade = SkinShade,
                EyeTone = EyeTone,
            };
        }

        public PlayerCosmetics Clamped()
        {
            return new PlayerCosmetics
            {
                PantsColor = Mathf.Clamp(PantsColor, 0, MAX_PANTS_OPTIONS - 1),
                ShirtColor = Mathf.Clamp(ShirtColor, 0, MAX_SHIRT_OPTIONS - 1),
                HairColor = Mathf.Clamp(HairColor, 0, MAX_HAIR_OPTIONS - 1),
                SkinTone = Mathf.Clamp(SkinTone, 0, MAX_SKIN_OPTIONS - 1),
                EyeColor = Mathf.Clamp(EyeColor, 0, MAX_EYE_OPTIONS - 1),
                PantsTone = Mathf.Clamp(PantsTone, 0, MAX_TONE_OPTIONS - 1),
                ShirtTone = Mathf.Clamp(ShirtTone, 0, MAX_TONE_OPTIONS - 1),
                HairTone = Mathf.Clamp(HairTone, 0, MAX_TONE_OPTIONS - 1),
                SkinShade = Mathf.Clamp(SkinShade, 0, MAX_TONE_OPTIONS - 1),
                EyeTone = Mathf.Clamp(EyeTone, 0, MAX_TONE_OPTIONS - 1),
            };
        }
        
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
                PantsTone = UnityEngine.Random.Range(0, MAX_TONE_OPTIONS),
                ShirtTone = UnityEngine.Random.Range(0, MAX_TONE_OPTIONS),
                HairTone = UnityEngine.Random.Range(0, MAX_TONE_OPTIONS),
                SkinShade = UnityEngine.Random.Range(0, MAX_TONE_OPTIONS),
                EyeTone = UnityEngine.Random.Range(0, MAX_TONE_OPTIONS),
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

        public int GetColor(CosmeticCategory category)
        {
            switch (category)
            {
                case CosmeticCategory.Pants: return PantsColor;
                case CosmeticCategory.Shirt: return ShirtColor;
                case CosmeticCategory.Hair: return HairColor;
                case CosmeticCategory.Eyes: return EyeColor;
                default: return SkinTone;
            }
        }

        public void SetColor(CosmeticCategory category, int value)
        {
            int clamped = Mathf.Clamp(value, 0, GetOptionCount(category) - 1);
            switch (category)
            {
                case CosmeticCategory.Pants:
                    PantsColor = clamped;
                    break;
                case CosmeticCategory.Shirt:
                    ShirtColor = clamped;
                    break;
                case CosmeticCategory.Hair:
                    HairColor = clamped;
                    break;
                case CosmeticCategory.Eyes:
                    EyeColor = clamped;
                    break;
                default:
                    SkinTone = clamped;
                    break;
            }
        }

        public int GetTone(CosmeticCategory category)
        {
            switch (category)
            {
                case CosmeticCategory.Pants: return PantsTone;
                case CosmeticCategory.Shirt: return ShirtTone;
                case CosmeticCategory.Hair: return HairTone;
                case CosmeticCategory.Eyes: return EyeTone;
                default: return SkinShade;
            }
        }

        public void SetTone(CosmeticCategory category, int value)
        {
            int clamped = Mathf.Clamp(value, 0, MAX_TONE_OPTIONS - 1);
            switch (category)
            {
                case CosmeticCategory.Pants:
                    PantsTone = clamped;
                    break;
                case CosmeticCategory.Shirt:
                    ShirtTone = clamped;
                    break;
                case CosmeticCategory.Hair:
                    HairTone = clamped;
                    break;
                case CosmeticCategory.Eyes:
                    EyeTone = clamped;
                    break;
                default:
                    SkinShade = clamped;
                    break;
            }
        }

        public int GetSelection(CosmeticCategory category)
        {
            return EncodeSelection(GetColor(category), GetTone(category));
        }

        public void SetSelection(CosmeticCategory category, int selection)
        {
            DecodeSelection(category, selection, out int color, out int tone);
            SetColor(category, color);
            SetTone(category, tone);
        }

        public static int GetOptionCount(CosmeticCategory category)
        {
            switch (category)
            {
                case CosmeticCategory.Pants: return MAX_PANTS_OPTIONS;
                case CosmeticCategory.Shirt: return MAX_SHIRT_OPTIONS;
                case CosmeticCategory.Hair: return MAX_HAIR_OPTIONS;
                case CosmeticCategory.Eyes: return MAX_EYE_OPTIONS;
                default: return MAX_SKIN_OPTIONS;
            }
        }

        public static Color[] GetPalette(CosmeticCategory category)
        {
            switch (category)
            {
                case CosmeticCategory.Pants: return PantsColors;
                case CosmeticCategory.Shirt: return ShirtColors;
                case CosmeticCategory.Hair: return HairColors;
                case CosmeticCategory.Eyes: return EyeColors;
                default: return SkinTones;
            }
        }

        public static int GetSelectionCount(CosmeticCategory category)
        {
            return GetOptionCount(category) * MAX_TONE_OPTIONS;
        }

        public static int EncodeSelection(int color, int tone)
        {
            int raw = Mathf.Clamp(color, 0, 7) * MAX_TONE_OPTIONS +
                      Mathf.Clamp(tone, 0, MAX_TONE_OPTIONS - 1);
            if (raw == DEFAULT_TONE)
                return 0;
            return raw < DEFAULT_TONE ? raw + 1 : raw;
        }

        public static void DecodeSelection(
            CosmeticCategory category,
            int selection,
            out int color,
            out int tone)
        {
            int clamped = Mathf.Clamp(selection, 0, GetSelectionCount(category) - 1);
            int raw = clamped == 0
                ? DEFAULT_TONE
                : clamped <= DEFAULT_TONE ? clamped - 1 : clamped;
            color = Mathf.Clamp(raw / MAX_TONE_OPTIONS, 0, GetOptionCount(category) - 1);
            tone = Mathf.Clamp(raw % MAX_TONE_OPTIONS, 0, MAX_TONE_OPTIONS - 1);
        }

        public static Color GetResolvedColor(CosmeticCategory category, int color, int tone)
        {
            Color[] palette = GetPalette(category);
            Color baseColor = palette[Mathf.Clamp(color, 0, palette.Length - 1)];
            int clampedTone = Mathf.Clamp(tone, 0, MAX_TONE_OPTIONS - 1);
            if (clampedTone == DEFAULT_TONE)
                return baseColor;

            Color.RGBToHSV(baseColor, out float hue, out float saturation, out float value);
            if (clampedTone < DEFAULT_TONE)
            {
                float amount = (float)clampedTone / DEFAULT_TONE;
                saturation *= Mathf.Lerp(0.45f, 1f, amount);
                value *= Mathf.Lerp(0.5f, 1f, amount);
            }
            else
            {
                float amount = (float)(clampedTone - DEFAULT_TONE) /
                               (MAX_TONE_OPTIONS - 1 - DEFAULT_TONE);
                saturation = Mathf.Lerp(
                    saturation,
                    Mathf.Clamp01(saturation * 1.2f + 0.08f),
                    amount);
                value = Mathf.Lerp(value, 1f, amount * 0.7f);
            }
            Color result = Color.HSVToRGB(hue, Mathf.Clamp01(saturation), Mathf.Clamp01(value));
            result.a = baseColor.a;
            return result;
        }

        public static Color GetResolvedColor(CosmeticCategory category, int selection)
        {
            DecodeSelection(category, selection, out int color, out int tone);
            return GetResolvedColor(category, color, tone);
        }
        
        /// <summary>
        /// Serialize one hue byte and one tone byte per category. Hue bytes come
        /// first so older receivers can still read the original five-byte form.
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
                (byte)PantsTone,
                (byte)ShirtTone,
                (byte)HairTone,
                (byte)SkinShade,
                (byte)EyeTone,
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
                PantsTone = data.Length > 5
                    ? Mathf.Clamp(data[5], 0, MAX_TONE_OPTIONS - 1)
                    : DEFAULT_TONE,
                ShirtTone = data.Length > 6
                    ? Mathf.Clamp(data[6], 0, MAX_TONE_OPTIONS - 1)
                    : DEFAULT_TONE,
                HairTone = data.Length > 7
                    ? Mathf.Clamp(data[7], 0, MAX_TONE_OPTIONS - 1)
                    : DEFAULT_TONE,
                SkinShade = data.Length > 8
                    ? Mathf.Clamp(data[8], 0, MAX_TONE_OPTIONS - 1)
                    : DEFAULT_TONE,
                EyeTone = data.Length > 9
                    ? Mathf.Clamp(data[9], 0, MAX_TONE_OPTIONS - 1)
                    : DEFAULT_TONE,
            };
        }
        
        public override string ToString()
        {
            return $"Cosmetics(Pants={PantsColor}/{PantsTone}, " +
                   $"Shirt={ShirtColor}/{ShirtTone}, Hair={HairColor}/{HairTone}, " +
                   $"Skin={SkinTone}/{SkinShade}, Eyes={EyeColor}/{EyeTone})";
        }
        
        public bool Equals(PlayerCosmetics other)
        {
            if (other == null) return false;
            return PantsColor == other.PantsColor &&
                   ShirtColor == other.ShirtColor &&
                   HairColor == other.HairColor &&
                   SkinTone == other.SkinTone &&
                   EyeColor == other.EyeColor &&
                   PantsTone == other.PantsTone &&
                   ShirtTone == other.ShirtTone &&
                   HairTone == other.HairTone &&
                   SkinShade == other.SkinShade &&
                   EyeTone == other.EyeTone;
        }
    }
}

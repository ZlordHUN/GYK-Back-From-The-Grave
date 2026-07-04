using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Applies cosmetic colors to a player character.
    /// Uses material property blocks for efficient per-instance coloring.
    /// Falls back to simple tinting if the custom shader is not available.
    /// </summary>
    public class PlayerCosmeticsDriver : MonoBehaviour
    {
        private PlayerCosmetics _cosmetics;
        private List<Renderer> _renderers = new List<Renderer>();
        private bool _initialized = false;
        
        // Tint multiplier (how strong the color change is)
        private const float TINT_STRENGTH = 0.3f;
        
        // Shader property IDs for custom shader (if available)
        private static int _pantsId = Shader.PropertyToID("_Pants");
        private static int _shirtId = Shader.PropertyToID("_Shirt");
        private static int _hairId = Shader.PropertyToID("_Hair");
        private static int _skinId = Shader.PropertyToID("_Skin");
        private static int _eyesId = Shader.PropertyToID("_Eyes");
        private static int _tintColorId = Shader.PropertyToID("_Color");
        
        // Track original colors for reset
        private Dictionary<Renderer, Color> _originalColors = new Dictionary<Renderer, Color>();
        
        public PlayerCosmetics Cosmetics => _cosmetics;
        
        private void Awake()
        {
            _cosmetics = PlayerCosmetics.Default;
        }
        
        private void Start()
        {
            Initialize();
        }
        
        /// <summary>
        /// Initialize the driver by finding all renderers
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;
            
            _renderers.Clear();
            _originalColors.Clear();
            
            // Find all renderers in the player hierarchy
            var allRenderers = GetComponentsInChildren<Renderer>(includeInactive: true);
            
            // Filter out non-player parts
            string[] exclusions = new string[]
            {
                "shadow", "fx", "tool", "garlic", "water", "fish", "bobber", 
                "overhead", "shard", "mask", "effect", "particle", "light"
            };
            
            foreach (var renderer in allRenderers)
            {
                string path = GetHierarchyPath(renderer.transform).ToLower();
                
                bool excluded = false;
                foreach (var exc in exclusions)
                {
                    if (path.Contains(exc))
                    {
                        excluded = true;
                        break;
                    }
                }
                
                if (!excluded)
                {
                    _renderers.Add(renderer);
                    
                    // Store original color
                    if (renderer.material != null && renderer.material.HasProperty(_tintColorId))
                    {
                        _originalColors[renderer] = renderer.material.color;
                    }
                }
            }
            
            _initialized = true;
            CoopMod.Logger.LogInfo($"[PlayerCosmeticsDriver] Initialized with {_renderers.Count} renderers");
        }
        
        /// <summary>
        /// Set and apply cosmetics
        /// </summary>
        public void SetCosmetics(PlayerCosmetics cosmetics)
        {
            if (cosmetics == null)
            {
                cosmetics = PlayerCosmetics.Default;
            }
            
            _cosmetics = cosmetics;
            Apply();
        }
        
        /// <summary>
        /// Set cosmetics by individual values
        /// </summary>
        public void SetCosmetics(int pants, int shirt, int hair, int skin, int eyes)
        {
            _cosmetics = new PlayerCosmetics
            {
                PantsColor = pants,
                ShirtColor = shirt,
                HairColor = hair,
                SkinTone = skin,
                EyeColor = eyes,
            };
            Apply();
        }
        
        /// <summary>
        /// Apply current cosmetics to all renderers
        /// </summary>
        public void Apply()
        {
            if (!_initialized)
            {
                Initialize();
            }
            
            // Calculate a blend color based on shirt and pants (most visible)
            Color blendColor = CalculateBlendColor();
            
            foreach (var renderer in _renderers)
            {
                if (renderer == null) continue;
                
                try
                {
                    // Try to apply via MaterialPropertyBlock (preferred, doesn't create material instances)
                    var mpb = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(mpb);
                    
                    // Set custom shader properties if they exist
                    mpb.SetInt(_pantsId, _cosmetics.PantsColor);
                    mpb.SetInt(_shirtId, _cosmetics.ShirtColor);
                    mpb.SetInt(_hairId, _cosmetics.HairColor);
                    mpb.SetInt(_skinId, _cosmetics.SkinTone);
                    mpb.SetInt(_eyesId, _cosmetics.EyeColor);
                    
                    // Apply a subtle tint for visibility (works with any shader)
                    Color originalColor = _originalColors.ContainsKey(renderer) 
                        ? _originalColors[renderer] 
                        : Color.white;
                    Color tintedColor = Color.Lerp(originalColor, blendColor, TINT_STRENGTH);
                    mpb.SetColor(_tintColorId, tintedColor);
                    
                    renderer.SetPropertyBlock(mpb);
                }
                catch (Exception ex)
                {
                    CoopMod.Logger.LogWarning($"[PlayerCosmeticsDriver] Error applying cosmetics to {renderer.name}: {ex.Message}");
                }
            }
            
            CoopMod.Logger.LogInfo($"[PlayerCosmeticsDriver] Applied {_cosmetics}");
        }
        
        /// <summary>
        /// Reset to original colors
        /// </summary>
        public void Reset()
        {
            _cosmetics = PlayerCosmetics.Default;
            
            foreach (var renderer in _renderers)
            {
                if (renderer == null) continue;
                
                try
                {
                    var mpb = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(mpb);
                    
                    if (_originalColors.TryGetValue(renderer, out Color original))
                    {
                        mpb.SetColor(_tintColorId, original);
                    }
                    
                    mpb.SetInt(_pantsId, 0);
                    mpb.SetInt(_shirtId, 0);
                    mpb.SetInt(_hairId, 0);
                    mpb.SetInt(_skinId, 0);
                    mpb.SetInt(_eyesId, 0);
                    
                    renderer.SetPropertyBlock(mpb);
                }
                catch { }
            }
        }
        
        /// <summary>
        /// Calculate a blended tint color from the cosmetics
        /// </summary>
        private Color CalculateBlendColor()
        {
            // Blend shirt and pants colors (most visible parts)
            Color pants = PlayerCosmetics.PantsColors[Mathf.Clamp(_cosmetics.PantsColor, 0, PlayerCosmetics.PantsColors.Length - 1)];
            Color shirt = PlayerCosmetics.ShirtColors[Mathf.Clamp(_cosmetics.ShirtColor, 0, PlayerCosmetics.ShirtColors.Length - 1)];
            
            // Weighted blend favoring shirt (more visible)
            return Color.Lerp(pants, shirt, 0.6f);
        }
        
        /// <summary>
        /// Get the full hierarchy path of a transform
        /// </summary>
        private static string GetHierarchyPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            while (t.parent != null)
            {
                t = t.parent;
                sb.Insert(0, t.name + "/");
            }
            return sb.ToString();
        }
        
        private void OnDestroy()
        {
            Reset();
            _renderers.Clear();
            _originalColors.Clear();
        }
    }
}

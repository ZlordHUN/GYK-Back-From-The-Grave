using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Applies palette replacement to one player. A bundled shader is used when it
    /// is compatible with the active renderer; otherwise the driver recolors sprite
    /// atlas textures on the CPU while retaining the vanilla materials and shaders.
    /// </summary>
    [DefaultExecutionOrder(20000)]
    public class PlayerCosmeticsDriver : MonoBehaviour
    {
        private enum RecolorBackend
        {
            None,
            Shader,
            CpuTexture,
        }

        private sealed class CachedCpuTexture
        {
            public Texture2D Texture;
            public int References;
        }

        private sealed class CpuSourceTextureData
        {
            public Texture2D SourceTexture;
            public Color32[] SourcePixels;
            public int[] MatchedPixelIndices;
            public byte[] MatchedPaletteRows;
            public int References;
            public long LastUseSequence;
            public readonly Dictionary<int, CachedCpuTexture> Variants =
                new Dictionary<int, CachedCpuTexture>();
        }

        private sealed class CpuRendererState
        {
            public CpuSourceTextureData Source;
            public CachedCpuTexture Variant;
            public int CosmeticsKey = -1;
        }

        private const string WindowsShaderResource =
            "GraveyardKeeperCoop.Assets.CharacterCustomization.gykmp.assetbundle";
        private const string WindowsOpenGlShaderResource =
            "GraveyardKeeperCoop.Assets.CharacterCustomization.gykmp-windows-gl.assetbundle";
        private const string OpenGlShaderResource =
            "GraveyardKeeperCoop.Assets.CharacterCustomization.gykmp-linux.assetbundle";
        private const string ColorMapResource =
            "GraveyardKeeperCoop.Assets.CharacterCustomization.playercolors.png";
        private const string ShaderAsset = "assets/shaders/playercolors.shader";
        private const float MatchEpsilon = 0.0005f;
        private const int MaxRetainedCpuSourceTextures = 6;
        private const int EyeIrisPaletteRow = 0;
        private const int EyeWhitePaletteRow = 1;

        private static readonly int PantsId = Shader.PropertyToID("_Pants");
        private static readonly int ShirtId = Shader.PropertyToID("_Shirt");
        private static readonly int HairId = Shader.PropertyToID("_Hair");
        private static readonly int SkinId = Shader.PropertyToID("_Skin");
        private static readonly int EyesId = Shader.PropertyToID("_Eyes");
        private static readonly int MainTextureId = Shader.PropertyToID("_MainTex");
        private static readonly int ColorMapId = Shader.PropertyToID("_ColormapTex");
        private static readonly int ColorLookupId = Shader.PropertyToID("_ColorLookupTex");
        private static readonly int RowCountId = Shader.PropertyToID("_RowCount");
        private static readonly int ColumnCountId = Shader.PropertyToID("_ColCount");
        private static readonly int MatchEpsilonId = Shader.PropertyToID("_MatchEps");
        private static readonly int AboveAmbientColourId = Shader.PropertyToID("_AboveAmbientColour");
        private static readonly int AttenuationExponentId = Shader.PropertyToID("_AttenuationExponent");
        private static readonly int LightSensitivityId = Shader.PropertyToID("_LightSensitivity");
        private static readonly int AdditionalColourId = Shader.PropertyToID("_AdditionalColour");
        private static readonly int AdditionalColourIntensityId = Shader.PropertyToID("_AdditionalColourIntensity");
        private static readonly int AlphaMultiplyId = Shader.PropertyToID("_AlphaMultiply");

        private static readonly Dictionary<Texture2D, CpuSourceTextureData> CpuSourceTextures =
            new Dictionary<Texture2D, CpuSourceTextureData>();
        private static readonly Dictionary<int, int> PaletteRowLookup =
            new Dictionary<int, int>();
        private static readonly Dictionary<int, Color32[]> CpuScratchBuffers =
            new Dictionary<int, Color32[]>();
        private static int _activeCpuCacheUsers;
        private static long _cpuCacheUseSequence;

        private PlayerCosmetics _cosmetics;
        private readonly List<SpriteRenderer> _renderers = new List<SpriteRenderer>();
        private readonly Dictionary<Renderer, Shader> _originalShaders =
            new Dictionary<Renderer, Shader>();
        private readonly Dictionary<Renderer, int> _originalRenderQueues =
            new Dictionary<Renderer, int>();
        private readonly Dictionary<Renderer, string[]> _originalKeywords =
            new Dictionary<Renderer, string[]>();
        private readonly Dictionary<Renderer, MaterialPropertyBlock> _originalPropertyBlocks =
            new Dictionary<Renderer, MaterialPropertyBlock>();
        private readonly Dictionary<SpriteRenderer, Material> _shaderMaterials =
            new Dictionary<SpriteRenderer, Material>();
        private readonly Dictionary<SpriteRenderer, CpuRendererState> _cpuRendererStates =
            new Dictionary<SpriteRenderer, CpuRendererState>();
        private readonly Dictionary<SpriteRenderer, Texture2D> _cpuAssignedTextures =
            new Dictionary<SpriteRenderer, Texture2D>();
        private readonly MaterialPropertyBlock _cpuPropertyBlock = new MaterialPropertyBlock();

        private PlayerComponent _playerComponent;
        private Color _lastAdditionalColour;
        private bool _hasAdditionalColour;
        private bool _initialized;
        private bool _registeredCpuCacheUser;
        private RecolorBackend _backend;

        private static Shader _shader;
        private static Texture2D _colorMap;
        private static Texture2D _colorLookup;
        private static AssetBundle _assetBundle;
        private static Color32[] _sourcePaletteRows;
        private static Color32[] _replacementPalette;
        private static int _paletteWidth;
        private static bool _paletteLoadAttempted;
        private static bool _paletteAvailable;
        private static bool _shaderLoadAttempted;
        private static bool _shaderAvailable;

        public PlayerCosmetics Cosmetics => _cosmetics;
        public bool IsAvailable => _backend != RecolorBackend.None;
        public bool LastApplySucceeded { get; private set; }
        public string ActiveBackend => _backend.ToString();

        private void Awake()
        {
            _cosmetics = PlayerCosmetics.Default;
        }

        private void Start()
        {
            Initialize();
        }

        private void LateUpdate()
        {
            if (!_initialized)
                return;

            if (_backend == RecolorBackend.CpuTexture)
            {
                long profilerStart = Utils.FrameProfiler.BeginSection();
                try
                {
                    // Animator and SkinChanger can restore the SpriteRenderer's atlas
                    // binding while walking. Validate the real property block every
                    // frame, after those systems have finished, and repair only when
                    // the override was displaced.
                    RefreshCpuTextureOverrides(false, true);
                }
                finally
                {
                    Utils.FrameProfiler.EndSection("Cosmetics.CpuUpdate", profilerStart);
                }
                return;
            }

            if (_backend != RecolorBackend.Shader || _renderers.Count == 0)
                return;

            if (_playerComponent == null)
                _playerComponent = GetComponent<PlayerComponent>();

            Color additionalColour = _playerComponent != null
                ? _playerComponent.player_additional_color
                : Color.black;
            if (_hasAdditionalColour && additionalColour == _lastAdditionalColour)
                return;
            _lastAdditionalColour = additionalColour;
            _hasAdditionalColour = true;
            RefreshShaderAdditionalColour(additionalColour);
        }

        public void Initialize()
        {
            if (_initialized)
                return;

            _initialized = true;
            LastApplySucceeded = false;
            _playerComponent = GetComponent<PlayerComponent>();
            _hasAdditionalColour = false;

            if (!EnsurePaletteResources())
                return;

            CollectEligibleRenderers();
            if (_renderers.Count == 0)
            {
                CoopMod.Logger.LogWarning(
                    "[PlayerCosmeticsDriver] No eligible player SpriteRenderers were found");
                return;
            }

            if (EnsureShaderResources() && TryInstallShaderBackend())
            {
                _backend = RecolorBackend.Shader;
                CoopMod.Logger.LogInfo(
                    $"[PlayerCosmeticsDriver] Installed palette shader on {_renderers.Count} renderers");
                return;
            }

            _backend = RecolorBackend.CpuTexture;
            RegisterCpuCacheUser();
            CoopMod.Logger.LogInfo(
                $"[PlayerCosmeticsDriver] Using CPU texture recoloring on {_renderers.Count} " +
                $"renderers ({Application.platform}, {SystemInfo.graphicsDeviceVersion})");
        }

        public void SetCosmetics(PlayerCosmetics cosmetics)
        {
            _cosmetics = (cosmetics ?? PlayerCosmetics.Default).Clamped();
            Apply();
        }

        public void SetCosmetics(int pants, int shirt, int hair, int skin, int eyes)
        {
            SetCosmetics(new PlayerCosmetics
            {
                PantsColor = pants,
                ShirtColor = shirt,
                HairColor = hair,
                SkinTone = skin,
                EyeColor = eyes,
            });
        }

        public void Apply()
        {
            TryApply();
        }

        /// <summary>
        /// Applies the current selection and reports whether at least one player
        /// renderer accepted it.
        /// </summary>
        public bool TryApply()
        {
            if (!_initialized)
                Initialize();

            if (_backend == RecolorBackend.None)
            {
                LastApplySucceeded = false;
                return false;
            }

            int applied = _backend == RecolorBackend.Shader
                ? ApplyShaderCosmetics()
                : ApplyCpuCosmetics();
            LastApplySucceeded = applied > 0;

            if (LastApplySucceeded)
            {
                CoopMod.Logger.LogInfo(
                    $"[PlayerCosmeticsDriver] Applied {_cosmetics} via {_backend} " +
                    $"to {applied} renderers");
            }
            else
            {
                CoopMod.Logger.LogWarning(
                    $"[PlayerCosmeticsDriver] Could not apply {_cosmetics} via {_backend}");
            }

            return LastApplySucceeded;
        }

        public void Reset()
        {
            _cosmetics = PlayerCosmetics.Default;
            TryApply();
        }

        private void CollectEligibleRenderers()
        {
            _renderers.Clear();
            _originalShaders.Clear();
            _originalRenderQueues.Clear();
            _originalKeywords.Clear();
            _originalPropertyBlocks.Clear();
            _shaderMaterials.Clear();

            foreach (SpriteRenderer renderer in GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (renderer == null || ShouldExcludeRenderer(renderer))
                    continue;

                _renderers.Add(renderer);
                MaterialPropertyBlock originalBlock = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(originalBlock);
                _originalPropertyBlocks[renderer] = originalBlock;
            }
        }

        private static bool ShouldExcludeRenderer(SpriteRenderer renderer)
        {
            string path = GetHierarchyPath(renderer.transform).ToLowerInvariant();
            string[] exclusions =
            {
                "shadow", "fx layer", "tool", "garlic", "water", "fish", "bobber",
                "overhead", "shard", "mask", "effect", "particle", "light"
            };
            foreach (string exclusion in exclusions)
            {
                if (path.Contains(exclusion))
                    return true;
            }
            return false;
        }

        private bool TryInstallShaderBackend()
        {
            try
            {
                foreach (SpriteRenderer renderer in _renderers)
                {
                    Material material = renderer.material;
                    if (material == null)
                        continue;
                    _originalShaders[renderer] = material.shader;
                    _originalRenderQueues[renderer] = material.renderQueue;
                    _originalKeywords[renderer] = material.shaderKeywords;
                    _shaderMaterials[renderer] = material;
                }

                foreach (SpriteRenderer renderer in _renderers)
                {
                    Material material = renderer.material;
                    if (material == null)
                        continue;

                    int renderQueue = material.renderQueue;
                    string[] keywords = material.shaderKeywords;
                    Color aboveAmbientColour = material.HasProperty(AboveAmbientColourId)
                        ? material.GetColor(AboveAmbientColourId)
                        : new Color(0f, 0f, 0f, 0.3f);
                    float attenuationExponent = material.HasProperty(AttenuationExponentId)
                        ? material.GetFloat(AttenuationExponentId)
                        : 2f;
                    float lightSensitivity = material.HasProperty(LightSensitivityId)
                        ? material.GetFloat(LightSensitivityId)
                        : 1f;
                    float additionalColourIntensity = material.HasProperty(AdditionalColourIntensityId)
                        ? material.GetFloat(AdditionalColourIntensityId)
                        : 0.5f;
                    float alphaMultiply = material.HasProperty(AlphaMultiplyId)
                        ? material.GetFloat(AlphaMultiplyId)
                        : 1f;
                    material.shader = _shader;
                    material.renderQueue = renderQueue;
                    material.shaderKeywords = keywords;
                    material.SetColor(AboveAmbientColourId, aboveAmbientColour);
                    material.SetFloat(AttenuationExponentId, attenuationExponent);
                    material.SetFloat(LightSensitivityId, lightSensitivity);
                    material.SetFloat(AdditionalColourIntensityId, additionalColourIntensity);
                    material.SetFloat(AlphaMultiplyId, alphaMultiply);
                    material.SetColor(AdditionalColourId, Color.black);
                    SetShaderCosmetics(material);
                }
                return true;
            }
            catch (Exception ex)
            {
                RestoreOriginalRenderState();
                CoopMod.Logger.LogWarning(
                    $"[PlayerCosmeticsDriver] Palette shader installation failed; " +
                    $"using the CPU fallback: {ex.Message}");
                return false;
            }
        }

        private int ApplyShaderCosmetics()
        {
            if (_playerComponent == null)
                _playerComponent = GetComponent<PlayerComponent>();
            Color additionalColour = _playerComponent != null
                ? _playerComponent.player_additional_color
                : Color.black;
            int applied = 0;
            foreach (SpriteRenderer renderer in _renderers)
            {
                if (renderer == null)
                    continue;

                try
                {
                    if (!_shaderMaterials.TryGetValue(renderer, out Material material))
                        material = renderer.material;
                    if (material == null)
                        continue;
                    SetShaderCosmetics(material);
                    applied++;
                }
                catch (Exception ex)
                {
                    CoopMod.Logger.LogWarning(
                        $"[PlayerCosmeticsDriver] Error applying shader cosmetics to " +
                        $"{renderer.name}: {ex.Message}");
                }
            }
            RefreshShaderAdditionalColour(additionalColour);
            return applied;
        }

        private void SetShaderCosmetics(Material material)
        {
            material.SetTexture(ColorMapId, _colorMap);
            material.SetTexture(ColorLookupId, _colorLookup);
            material.SetInt(RowCountId, _colorMap.height);
            material.SetInt(ColumnCountId, _colorMap.width);
            material.SetFloat(MatchEpsilonId, MatchEpsilon);
            material.SetInt(PantsId, _cosmetics.GetSelection(CosmeticCategory.Pants));
            material.SetInt(ShirtId, _cosmetics.GetSelection(CosmeticCategory.Shirt));
            material.SetInt(HairId, _cosmetics.GetSelection(CosmeticCategory.Hair));
            material.SetInt(SkinId, _cosmetics.GetSelection(CosmeticCategory.Skin));
            material.SetInt(EyesId, _cosmetics.GetSelection(CosmeticCategory.Eyes));
        }

        private void RefreshShaderAdditionalColour(Color additionalColour)
        {
            foreach (SpriteRenderer renderer in _renderers)
            {
                if (renderer == null)
                    continue;

                try
                {
                    if (!_shaderMaterials.TryGetValue(renderer, out Material material))
                        material = renderer.material;
                    material?.SetColor(AdditionalColourId, additionalColour);
                }
                catch (Exception ex)
                {
                    CoopMod.Logger.LogWarning(
                        $"[PlayerCosmeticsDriver] Error applying shader color effect to " +
                        $"{renderer.name}: {ex.Message}");
                }
            }
        }

        private int ApplyCpuCosmetics()
        {
            return RefreshCpuTextureOverrides(true, true);
        }

        private int RefreshCpuTextureOverrides(bool force, bool verifyAssignments)
        {
            if (verifyAssignments)
                PruneDestroyedCpuRendererStates();

            int cosmeticsKey = GetCosmeticsKey(_cosmetics);
            int applied = 0;
            foreach (SpriteRenderer renderer in _renderers)
            {
                if (renderer == null || renderer.sprite == null || renderer.sprite.texture == null)
                    continue;

                try
                {
                    Texture2D sourceTexture = renderer.sprite.texture;

                    // Vanilla selections need no replacement texture. Avoid the
                    // expensive SpriteAtlas readback until customization is used.
                    if (cosmeticsKey == 0)
                    {
                        if (!force &&
                            !_cpuAssignedTextures.ContainsKey(renderer) &&
                            !_cpuRendererStates.ContainsKey(renderer))
                        {
                            applied++;
                            continue;
                        }

                        bool hadAssignedTexture = _cpuAssignedTextures.ContainsKey(renderer);
                        if (_cpuRendererStates.TryGetValue(renderer, out CpuRendererState oldState))
                        {
                            ReleaseCpuRendererState(oldState);
                            _cpuRendererStates.Remove(renderer);
                        }

                        if (hadAssignedTexture)
                            RestoreRendererPropertyBlock(renderer);
                        applied++;
                        continue;
                    }

                    if (!force &&
                        _cpuRendererStates.TryGetValue(renderer, out CpuRendererState existingState) &&
                        existingState.Source != null &&
                        existingState.Source.SourceTexture == sourceTexture &&
                        existingState.CosmeticsKey == cosmeticsKey)
                    {
                        Texture2D expectedTexture = existingState.Variant?.Texture;
                        bool hasExpectedAssignment = expectedTexture != null &&
                            _cpuAssignedTextures.TryGetValue(renderer, out Texture2D assignedTexture) &&
                            assignedTexture == expectedTexture;
                        bool sourceHasNoMatches =
                            existingState.Source.MatchedPixelIndices.Length == 0;
                        if (sourceHasNoMatches ||
                            (hasExpectedAssignment &&
                             (!verifyAssignments ||
                              RendererUsesMainTexture(renderer, expectedTexture))))
                        {
                            if (!sourceHasNoMatches)
                                applied++;
                            continue;
                        }
                    }

                    CpuRendererState state = GetCpuRendererState(renderer, sourceTexture);
                    if (state.Source.MatchedPixelIndices.Length == 0)
                    {
                        state.CosmeticsKey = cosmeticsKey;
                        if (_cpuAssignedTextures.ContainsKey(renderer))
                            RestoreRendererPropertyBlock(renderer);
                        continue;
                    }

                    Texture2D desiredTexture = GetCpuTexture(state, cosmeticsKey);
                    if (desiredTexture == null)
                    {
                        if (_cpuAssignedTextures.ContainsKey(renderer))
                            RestoreRendererPropertyBlock(renderer);
                        continue;
                    }

                    renderer.GetPropertyBlock(_cpuPropertyBlock);
                    _cpuPropertyBlock.SetTexture(MainTextureId, desiredTexture);
                    renderer.SetPropertyBlock(_cpuPropertyBlock);
                    _cpuAssignedTextures[renderer] = desiredTexture;
                    applied++;
                }
                catch (Exception ex)
                {
                    Texture2D sourceTexture = renderer.sprite?.texture;
                    if (sourceTexture != null && _cpuAssignedTextures.ContainsKey(renderer))
                        RestoreRendererPropertyBlock(renderer);
                    CoopMod.Logger.LogWarning(
                        $"[PlayerCosmeticsDriver] Error applying CPU cosmetics to " +
                        $"{renderer.name}: {ex.Message}");
                }
            }
            return applied;
        }

        private CpuRendererState GetCpuRendererState(
            SpriteRenderer renderer,
            Texture2D sourceTexture)
        {
            if (_cpuRendererStates.TryGetValue(renderer, out CpuRendererState state) &&
                state.Source != null && state.Source.SourceTexture == sourceTexture)
            {
                return state;
            }

            if (state != null)
            {
                ReleaseCpuRendererState(state);
            }

            state = new CpuRendererState
            {
                Source = AcquireCpuSourceTexture(sourceTexture),
            };
            _cpuRendererStates[renderer] = state;
            return state;
        }

        private static Texture2D GetCpuTexture(CpuRendererState state, int cosmeticsKey)
        {
            if (state.CosmeticsKey == cosmeticsKey)
            {
                return state.Variant != null
                    ? state.Variant.Texture
                    : state.Source.SourceTexture;
            }

            ReleaseCpuVariant(state);
            if (cosmeticsKey == 0 || state.Source.MatchedPixelIndices.Length == 0)
            {
                state.CosmeticsKey = cosmeticsKey;
                return state.Source.SourceTexture;
            }

            CachedCpuTexture variant = AcquireCpuVariant(state.Source, cosmeticsKey);
            if (variant?.Texture == null)
            {
                state.CosmeticsKey = -1;
                return null;
            }

            state.Variant = variant;
            state.CosmeticsKey = cosmeticsKey;
            return variant.Texture;
        }

        private static CpuSourceTextureData AcquireCpuSourceTexture(Texture2D sourceTexture)
        {
            if (CpuSourceTextures.TryGetValue(sourceTexture, out CpuSourceTextureData cached))
            {
                cached.References++;
                cached.LastUseSequence = ++_cpuCacheUseSequence;
                return cached;
            }

            Color32[] pixels = ReadTexturePixels(sourceTexture);
            List<int> matchedIndices = new List<int>();
            List<byte> matchedRows = new List<byte>();
            for (int index = 0; index < pixels.Length; index++)
            {
                int row = FindPaletteRow(pixels[index]);
                if (row < 0)
                    continue;
                matchedIndices.Add(index);
                matchedRows.Add((byte)row);
            }

            cached = new CpuSourceTextureData
            {
                SourceTexture = sourceTexture,
                SourcePixels = pixels,
                MatchedPixelIndices = matchedIndices.ToArray(),
                MatchedPaletteRows = matchedRows.ToArray(),
                References = 1,
                LastUseSequence = ++_cpuCacheUseSequence,
            };
            CpuSourceTextures[sourceTexture] = cached;
            PruneCpuSourceCache();
            CoopMod.Logger.LogInfo(
                $"[PlayerCosmeticsDriver] Cached CPU palette data for " +
                $"'{sourceTexture.name}' ({sourceTexture.width}x{sourceTexture.height}, " +
                $"instance={sourceTexture.GetInstanceID()}, " +
                $"{cached.MatchedPixelIndices.Length} matched pixels)");
            return cached;
        }

        private static CachedCpuTexture AcquireCpuVariant(
            CpuSourceTextureData source,
            int cosmeticsKey)
        {
            if (source.Variants.TryGetValue(cosmeticsKey, out CachedCpuTexture cached))
            {
                cached.References++;
                return cached;
            }

            // Animation pages are frequently unused for a few frames. Retain the
            // current variant for each page, but discard older unreferenced color
            // selections before creating a replacement so slider previews cannot
            // grow the cache without bound.
            PruneUnreferencedCpuVariants(source);

            int pixelCount = source.SourcePixels.Length;
            if (!CpuScratchBuffers.TryGetValue(pixelCount, out Color32[] scratchPixels))
            {
                scratchPixels = new Color32[pixelCount];
                CpuScratchBuffers[pixelCount] = scratchPixels;
            }
            Array.Copy(source.SourcePixels, scratchPixels, pixelCount);

            for (int match = 0; match < source.MatchedPixelIndices.Length; match++)
            {
                int pixelIndex = source.MatchedPixelIndices[match];
                int paletteRow = source.MatchedPaletteRows[match];
                int paletteColumn = GetPaletteColumn(paletteRow, cosmeticsKey);
                if (paletteColumn == 0)
                    continue;

                Color32 replacement = _replacementPalette[
                    paletteRow * _paletteWidth + paletteColumn];
                replacement.a = source.SourcePixels[pixelIndex].a;
                scratchPixels[pixelIndex] = replacement;
            }

            bool hasMipMaps = source.SourceTexture.mipmapCount > 1;
            Texture2D texture = null;
            try
            {
                texture = new Texture2D(
                    source.SourceTexture.width,
                    source.SourceTexture.height,
                    TextureFormat.RGBA32,
                    hasMipMaps,
                    false)
                {
                    name = $"GYKMP_{source.SourceTexture.name}_{cosmeticsKey:X}",
                    filterMode = source.SourceTexture.filterMode,
                    wrapMode = source.SourceTexture.wrapMode,
                    anisoLevel = source.SourceTexture.anisoLevel,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                texture.SetPixels32(scratchPixels);
                texture.Apply(hasMipMaps, true);
            }
            catch
            {
                if (texture != null)
                    Destroy(texture);
                throw;
            }

            cached = new CachedCpuTexture
            {
                Texture = texture,
                References = 1,
            };
            source.Variants[cosmeticsKey] = cached;
            return cached;
        }

        private static void ReleaseCpuVariant(CpuRendererState state)
        {
            if (state == null || state.Source == null)
                return;
            if (state.Variant == null)
            {
                state.CosmeticsKey = -1;
                return;
            }

            CachedCpuTexture variant = state.Variant;
            state.Variant = null;
            state.CosmeticsKey = -1;
            variant.References = Math.Max(0, variant.References - 1);
        }

        private static void ReleaseCpuRendererState(CpuRendererState state)
        {
            if (state == null || state.Source == null)
                return;

            ReleaseCpuVariant(state);
            CpuSourceTextureData source = state.Source;
            state.Source = null;
            source.References = Math.Max(0, source.References - 1);
            source.LastUseSequence = ++_cpuCacheUseSequence;
            PruneCpuSourceCache();
        }

        private void PruneDestroyedCpuRendererStates()
        {
            List<SpriteRenderer> destroyed = null;
            foreach (KeyValuePair<SpriteRenderer, CpuRendererState> pair in _cpuRendererStates)
            {
                if (pair.Key != null)
                    continue;
                if (destroyed == null)
                    destroyed = new List<SpriteRenderer>();
                destroyed.Add(pair.Key);
                ReleaseCpuRendererState(pair.Value);
            }

            if (destroyed != null)
            {
                foreach (SpriteRenderer renderer in destroyed)
                {
                    _cpuRendererStates.Remove(renderer);
                    _cpuAssignedTextures.Remove(renderer);
                }
                _renderers.RemoveAll(renderer => renderer == null);
            }
        }

        private static void PruneUnreferencedCpuVariants(CpuSourceTextureData source)
        {
            List<int> staleKeys = null;
            foreach (KeyValuePair<int, CachedCpuTexture> pair in source.Variants)
            {
                if (pair.Value.References > 0)
                    continue;
                if (staleKeys == null)
                    staleKeys = new List<int>();
                staleKeys.Add(pair.Key);
            }

            if (staleKeys == null)
                return;
            foreach (int key in staleKeys)
            {
                CachedCpuTexture variant = source.Variants[key];
                if (variant.Texture != null)
                    Destroy(variant.Texture);
                source.Variants.Remove(key);
            }
        }

        private static void PruneCpuSourceCache()
        {
            while (CpuSourceTextures.Count > MaxRetainedCpuSourceTextures)
            {
                CpuSourceTextureData oldest = null;
                foreach (CpuSourceTextureData candidate in CpuSourceTextures.Values)
                {
                    if (candidate.References > 0 ||
                        (oldest != null &&
                         candidate.LastUseSequence >= oldest.LastUseSequence))
                    {
                        continue;
                    }
                    oldest = candidate;
                }

                if (oldest == null)
                    return;
                DestroyCpuSourceTexture(oldest);
            }
        }

        private static void DestroyCpuSourceTexture(CpuSourceTextureData source)
        {
            foreach (CachedCpuTexture variant in source.Variants.Values)
            {
                if (variant.Texture != null)
                    Destroy(variant.Texture);
            }
            source.Variants.Clear();
            CpuSourceTextures.Remove(source.SourceTexture);
        }

        private void RegisterCpuCacheUser()
        {
            if (_registeredCpuCacheUser)
                return;
            _registeredCpuCacheUser = true;
            _activeCpuCacheUsers++;
        }

        private void UnregisterCpuCacheUser()
        {
            if (!_registeredCpuCacheUser)
                return;
            _registeredCpuCacheUser = false;
            _activeCpuCacheUsers = Math.Max(0, _activeCpuCacheUsers - 1);
            if (_activeCpuCacheUsers != 0)
                return;

            foreach (CpuSourceTextureData source in CpuSourceTextures.Values)
            {
                foreach (CachedCpuTexture variant in source.Variants.Values)
                {
                    if (variant.Texture != null)
                        Destroy(variant.Texture);
                }
                source.Variants.Clear();
            }
            CpuSourceTextures.Clear();
            CpuScratchBuffers.Clear();
            PaletteRowLookup.Clear();
            _cpuCacheUseSequence = 0;
        }

        /// <summary>
        /// Returns the texture currently used to render a sprite. NGUI's
        /// UI2DSprite always samples Sprite.texture, so the wardrobe preview uses
        /// this to mirror the CPU atlas override through a matching runtime sprite.
        /// </summary>
        public bool TryGetDisplayTexture(SpriteRenderer renderer, out Texture2D texture)
        {
            texture = null;
            if (renderer == null || renderer.sprite == null)
                return false;

            if (_backend == RecolorBackend.CpuTexture &&
                _cpuAssignedTextures.TryGetValue(renderer, out Texture2D assigned) &&
                assigned != null)
            {
                texture = assigned;
                return true;
            }

            texture = renderer.sprite.texture;
            return texture != null;
        }

        private bool RendererUsesMainTexture(
            SpriteRenderer renderer,
            Texture2D expected)
        {
            renderer.GetPropertyBlock(_cpuPropertyBlock);
            return _cpuPropertyBlock.GetTexture(MainTextureId) == expected;
        }

        private void RestoreRendererPropertyBlock(SpriteRenderer renderer)
        {
            if (renderer == null)
                return;

            if (_originalPropertyBlocks.TryGetValue(
                    renderer,
                    out MaterialPropertyBlock originalBlock))
            {
                renderer.SetPropertyBlock(originalBlock);
            }
            else
            {
                renderer.SetPropertyBlock(null);
            }
            foreach (MaterialPropertyModifier modifier in
                     renderer.GetComponents<MaterialPropertyModifier>())
            {
                modifier.UpdateRenderer();
            }
            _cpuAssignedTextures.Remove(renderer);
        }

        private static int GetCosmeticsKey(PlayerCosmetics cosmetics)
        {
            if (cosmetics == null)
                return 0;
            return cosmetics.GetSelection(CosmeticCategory.Eyes) |
                   cosmetics.GetSelection(CosmeticCategory.Pants) << 6 |
                   cosmetics.GetSelection(CosmeticCategory.Shirt) << 12 |
                   cosmetics.GetSelection(CosmeticCategory.Hair) << 18 |
                   cosmetics.GetSelection(CosmeticCategory.Skin) << 24;
        }

        private static int GetPaletteColumn(int paletteRow, int cosmeticsKey)
        {
            if (paletteRow > 30)
                return cosmeticsKey >> 24 & 0x3F;
            if (paletteRow > 21)
                return cosmeticsKey >> 18 & 0x3F;
            if (paletteRow > 10)
                return cosmeticsKey >> 12 & 0x3F;
            if (paletteRow > 2)
                return cosmeticsKey >> 6 & 0x3F;
            return cosmeticsKey & 0x3F;
        }

        private static int FindPaletteRow(Color32 color)
        {
            int colorKey = color.r << 16 | color.g << 8 | color.b;
            if (PaletteRowLookup.TryGetValue(colorKey, out int cachedRow))
                return cachedRow;

            int bestRow = -1;
            int bestDistance = int.MaxValue;
            for (int row = 0; row < _sourcePaletteRows.Length; row++)
            {
                Color32 candidate = _sourcePaletteRows[row];
                int red = color.r - candidate.r;
                int green = color.g - candidate.g;
                int blue = color.b - candidate.b;
                int distance = red * red + green * green + blue * blue;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestRow = row;
                }
            }

            float byteDistanceLimit = MatchEpsilon * 255f * 255f;
            if (bestDistance > byteDistanceLimit)
                bestRow = -1;
            PaletteRowLookup[colorKey] = bestRow;
            return bestRow;
        }

        private static Color32[] ReadTexturePixels(Texture2D source)
        {
            try
            {
                return source.GetPixels32();
            }
            catch
            {
                // SpriteAtlas textures are normally non-readable. Round-trip through
                // a render target once, then cache the CPU pixels for later variants.
            }

            RenderTexture previous = RenderTexture.active;
            RenderTexture temporary = null;
            Texture2D readable = null;
            try
            {
                temporary = RenderTexture.GetTemporary(
                    source.width,
                    source.height,
                    0,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Default);
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                readable = new Texture2D(
                    source.width,
                    source.height,
                    TextureFormat.RGBA32,
                    false,
                    false);
                readable.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0, false);
                readable.Apply(false, false);
                return readable.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                if (temporary != null)
                    RenderTexture.ReleaseTemporary(temporary);
                if (readable != null)
                    Destroy(readable);
            }
        }

        private void RestoreOriginalRenderState()
        {
            foreach (SpriteRenderer renderer in _renderers)
            {
                if (renderer == null)
                    continue;

                try
                {
                    if (_originalShaders.TryGetValue(renderer, out Shader shader))
                    {
                        Material material = renderer.material;
                        material.shader = shader;
                        if (_originalRenderQueues.TryGetValue(renderer, out int renderQueue))
                            material.renderQueue = renderQueue;
                        if (_originalKeywords.TryGetValue(renderer, out string[] keywords))
                            material.shaderKeywords = keywords;
                    }
                    if (_backend == RecolorBackend.CpuTexture && renderer.sprite?.texture != null)
                    {
                        RestoreRendererPropertyBlock(renderer);
                    }
                    else if (_originalPropertyBlocks.TryGetValue(
                                 renderer,
                                 out MaterialPropertyBlock block))
                    {
                        renderer.SetPropertyBlock(block);
                    }
                }
                catch (Exception ex)
                {
                    CoopMod.Logger.LogWarning(
                        $"[PlayerCosmeticsDriver] Error restoring {renderer.name}: {ex.Message}");
                }
            }
            _cpuAssignedTextures.Clear();
        }

        private static bool TryGetShaderResource(out string resource)
        {
            string graphicsApi = SystemInfo.graphicsDeviceVersion ?? string.Empty;
            bool isDirect3D =
                graphicsApi.StartsWith("Direct3D", StringComparison.OrdinalIgnoreCase);
            bool isOpenGl =
                graphicsApi.StartsWith("OpenGL", StringComparison.OrdinalIgnoreCase);

            if (Application.platform == RuntimePlatform.WindowsPlayer && isDirect3D)
            {
                resource = WindowsShaderResource;
                return true;
            }

            // Asset bundles contain shader programs for only the first configured
            // graphics API. Proton is a Windows player using OpenGL, so it needs a
            // Windows-targeted bundle compiled exclusively for GLCore.
            if (Application.platform == RuntimePlatform.WindowsPlayer && isOpenGl)
            {
                resource = WindowsOpenGlShaderResource;
                return true;
            }

            if (Application.platform == RuntimePlatform.LinuxPlayer && isOpenGl)
            {
                resource = OpenGlShaderResource;
                return true;
            }

            resource = null;
            return false;
        }

        private static bool EnsureShaderResources()
        {
            if (_shaderLoadAttempted)
                return _shaderAvailable;
            _shaderLoadAttempted = true;

            if (!TryGetShaderResource(out string shaderResource))
                return false;

            try
            {
                byte[] bundleBytes = ReadEmbeddedResource(shaderResource);
                _assetBundle = AssetBundle.LoadFromMemory(bundleBytes);
                if (_assetBundle == null)
                    throw new InvalidOperationException("Unity could not load the embedded asset bundle");

                _shader = _assetBundle.LoadAsset<Shader>(ShaderAsset);
                if (_shader == null)
                    throw new InvalidOperationException($"Shader '{ShaderAsset}' was not found in the bundle");
                if (!_shader.isSupported)
                {
                    throw new PlatformNotSupportedException(
                        $"Shader '{_shader.name}' is unsupported by '{SystemInfo.graphicsDeviceVersion}'");
                }

                _shaderAvailable = true;
                CoopMod.Logger.LogInfo(
                    $"[PlayerCosmeticsDriver] Loaded shader '{_shader.name}' from '{shaderResource}'");
            }
            catch (Exception ex)
            {
                _shaderAvailable = false;
                CoopMod.Logger.LogWarning(
                    $"[PlayerCosmeticsDriver] Palette shader is unavailable; " +
                    $"using the CPU fallback: {ex.Message}");
            }
            return _shaderAvailable;
        }

        private static bool EnsurePaletteResources()
        {
            if (_paletteLoadAttempted)
                return _paletteAvailable;
            _paletteLoadAttempted = true;

            Texture2D sourceMap = null;
            try
            {
                byte[] colorMapBytes = ReadEmbeddedResource(ColorMapResource);
                sourceMap = new Texture2D(2, 2, TextureFormat.RGBA32, false, true)
                {
                    name = "PlayerCosmeticsSourceMap",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    anisoLevel = 0,
                };
                if (!ImageConversion.LoadImage(sourceMap, colorMapBytes, false))
                    throw new InvalidOperationException("Unity could not decode the embedded color map");

                _colorMap = BuildColorMap(sourceMap);
                _colorLookup = BuildColorLookup();
                _paletteAvailable = true;
                CoopMod.Logger.LogInfo(
                    $"[PlayerCosmeticsDriver] Loaded {_colorMap.width}x{_colorMap.height} color map");
            }
            catch (Exception ex)
            {
                _paletteAvailable = false;
                CoopMod.Logger.LogWarning(
                    $"[PlayerCosmeticsDriver] Character recoloring is unavailable; " +
                    $"leaving vanilla player materials unchanged: {ex.Message}");
            }
            finally
            {
                if (sourceMap != null)
                    Destroy(sourceMap);
            }
            return _paletteAvailable;
        }

        private static Texture2D BuildColorMap(Texture2D source)
        {
            int width = PlayerCosmetics.MAX_SELECTION_OPTIONS;
            Texture2D result = new Texture2D(
                width,
                source.height,
                TextureFormat.RGBA32,
                false,
                true)
            {
                name = "PlayerCosmeticsColorMap",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
            };

            _paletteWidth = width;
            _sourcePaletteRows = new Color32[source.height];
            _replacementPalette = new Color32[width * source.height];
            PaletteRowLookup.Clear();

            for (int row = 0; row < source.height; row++)
            {
                // The authored PNG is top-origin while Unity's texture API is
                // bottom-origin. Keep logical row zero as the eye palette row.
                Color sourceColor = source.GetPixel(0, source.height - 1 - row);
                CosmeticCategory category = GetCategoryForRow(row);
                Color[] palette = PlayerCosmetics.GetPalette(category);
                bool placeholder = IsPlaceholder(sourceColor);
                _sourcePaletteRows[row] = sourceColor;

                for (int column = 0; column < width; column++)
                {
                    Color replacement = sourceColor;
                    if (column > 0 &&
                        !placeholder &&
                        row != EyeWhitePaletteRow &&
                        palette != null)
                    {
                        PlayerCosmetics.DecodeSelection(
                            category,
                            column,
                            out int color,
                            out int tone);
                        Color target = PlayerCosmetics.GetResolvedColor(category, color, tone);
                        if (row == EyeIrisPaletteRow)
                        {
                            replacement = target;
                            replacement.a = sourceColor.a;
                        }
                        else
                        {
                            replacement = RecolorPreservingShade(
                                sourceColor,
                                palette[0],
                                target);
                        }
                    }
                    result.SetPixel(column, row, replacement);
                    _replacementPalette[row * width + column] = replacement;
                }
            }

            result.Apply(false, true);
            return result;
        }

        private static Texture2D BuildColorLookup()
        {
            const int lookupSize = 256;
            if (_sourcePaletteRows.Length >= byte.MaxValue)
            {
                throw new InvalidDataException(
                    "The player color lookup cannot encode more than 254 palette rows");
            }

            Texture2D lookup = new Texture2D(
                lookupSize,
                lookupSize,
                TextureFormat.RGBA32,
                false,
                true)
            {
                name = "PlayerCosmeticsColorLookup",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
            };

            Color32[] pixels = new Color32[lookupSize * lookupSize];
            for (int row = 0; row < _sourcePaletteRows.Length; row++)
            {
                Color32 source = _sourcePaletteRows[row];
                if (IsPlaceholder(source))
                    continue;

                // Non-placeholder palette colors have unique red/green pairs.
                // Store blue plus row+1; alpha distinguishes an empty lookup cell.
                int lookupIndex = source.g * lookupSize + source.r;
                if (pixels[lookupIndex].a != 0)
                {
                    throw new InvalidDataException(
                        $"Palette rows share lookup coordinates R={source.r}, G={source.g}");
                }
                pixels[lookupIndex] =
                    new Color32(source.b, (byte)(row + 1), 0, 255);
            }
            lookup.SetPixels32(pixels);
            lookup.Apply(false, true);
            return lookup;
        }

        private static CosmeticCategory GetCategoryForRow(int row)
        {
            if (row <= 2)
                return CosmeticCategory.Eyes;
            if (row <= 10)
                return CosmeticCategory.Pants;
            if (row <= 21)
                return CosmeticCategory.Shirt;
            if (row <= 30)
                return CosmeticCategory.Hair;
            return CosmeticCategory.Skin;
        }

        private static bool IsPlaceholder(Color color)
        {
            return color.r > 0.95f && color.g < 0.05f && color.b > 0.60f;
        }

        private static Color RecolorPreservingShade(Color source, Color baseColor, Color target)
        {
            Color.RGBToHSV(source, out _, out float sourceSaturation, out float sourceValue);
            Color.RGBToHSV(baseColor, out _, out float baseSaturation, out float baseValue);
            Color.RGBToHSV(target, out float targetHue, out float targetSaturation, out float targetValue);

            float valueScale = baseValue > 0.001f ? sourceValue / baseValue : 1f;
            float saturationScale = baseSaturation > 0.001f
                ? sourceSaturation / baseSaturation
                : 1f;
            Color recolored = Color.HSVToRGB(
                targetHue,
                Mathf.Clamp01(targetSaturation * saturationScale),
                Mathf.Clamp01(targetValue * valueScale));
            recolored.a = source.a;
            return recolored;
        }

        private static byte[] ReadEmbeddedResource(string resourceName)
        {
            using (Stream input = typeof(PlayerCosmeticsDriver).Assembly
                       .GetManifestResourceStream(resourceName))
            {
                if (input == null)
                    throw new FileNotFoundException("Embedded resource was not found", resourceName);

                using (MemoryStream output = new MemoryStream())
                {
                    input.CopyTo(output);
                    return output.ToArray();
                }
            }
        }

        private static string GetHierarchyPath(Transform transform)
        {
            StringBuilder result = new StringBuilder(transform.name);
            while (transform.parent != null)
            {
                transform = transform.parent;
                result.Insert(0, transform.name + "/");
            }
            return result.ToString();
        }

        private void OnDestroy()
        {
            RestoreOriginalRenderState();
            foreach (CpuRendererState state in _cpuRendererStates.Values)
                ReleaseCpuRendererState(state);
            _cpuRendererStates.Clear();
            UnregisterCpuCacheUser();
            _cpuAssignedTextures.Clear();
            _renderers.Clear();
            _originalShaders.Clear();
            _originalRenderQueues.Clear();
            _originalKeywords.Clear();
            _originalPropertyBlocks.Clear();
            _shaderMaterials.Clear();
        }
    }
}

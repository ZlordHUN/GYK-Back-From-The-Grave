using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using System.Reflection;
using Steamworks;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Patches
{
    public class SkipIntroCutscene
    {
        internal static FieldInfo meField;
        internal static FieldInfo needShowField;

        static SkipIntroCutscene()
        {
            meField = typeof(Intro).GetField("_me", BindingFlags.NonPublic | BindingFlags.Static);
            needShowField = typeof(Intro).GetField("need_show_first_intro", BindingFlags.Public | BindingFlags.Static);
        }

        internal static void ResetSkipStateForCurrentIntro()
        {
            Intro introInstance = meField != null ? meField.GetValue(null) as Intro : null;
            if (introInstance != null)
            {
                var handler = introInstance.gameObject.GetComponent<IntroSkipHandler>();
                if (handler != null)
                {
                    handler.ResetSkipState();
                }
            }
        }

        internal static Intro GetIntroInstance()
        {
            return meField != null ? meField.GetValue(null) as Intro : null;
        }

        internal static bool NeedShowFirstIntro()
        {
            if (needShowField != null)
                return (bool)needShowField.GetValue(null);
            return false;
        }
    }

    [HarmonyPatch(typeof(Intro), "Awake")]
    public class SkipIntroCutsceneAttach
    {
        static void Postfix(Intro __instance)
        {
            if (__instance.gameObject.GetComponent<IntroSkipHandler>() == null)
            {
                __instance.gameObject.AddComponent<IntroSkipHandler>();
                CoopMod.Logger.LogInfo("[IntroSkip] Attached skip handler to Intro");
            }
        }
    }

    public class IntroSkipHandler : MonoBehaviour
    {
        private const float HoldDuration = 3f;
        private const int TexSize = 32;
        private const float OuterRadius = 14f;
        private const float InnerRadius = 10f;

        private float holdStartTime = -1f;
        private bool isHolding;
        private bool skipTriggered;
        private float currentProgress;

        private Texture2D ringTexture;
        private GUIStyle promptLabelStyle;
        private bool texturesInitialized;
        private bool lastIntroEligibility;
        private float lastIntroSkipDebugLogTime = -100f;
        private float lastHoldDebugLogTime = -100f;

        // Button icon texture and UV data extracted from the NGUI font atlas
        private Texture2D iconAtlasTexture;
        private Rect iconUVRect;
        private bool iconReady;
        private bool iconAttempted;

        private static bool _skipIntroEventSubscribed;

        public void ResetSkipState()
        {
            skipTriggered = false;
            isHolding = false;
            holdStartTime = -1f;
            currentProgress = 0f;
            lastIntroEligibility = false;
            lastIntroSkipDebugLogTime = -100f;
            lastHoldDebugLogTime = -100f;
            CoopMod.Logger.LogInfo("[IntroSkip] Skip state reset for new intro playback");
        }

        void Update()
        {
            if (skipTriggered) return;

            Intro introInstance = SkipIntroCutscene.GetIntroInstance();
            bool needShow = SkipIntroCutscene.NeedShowFirstIntro();
            bool introEligible = introInstance != null && needShow && introInstance.gameObject.activeSelf;

            if (introEligible != lastIntroEligibility)
            {
                CoopMod.Logger.LogInfo($"[IntroSkipDebug] introEligible={introEligible}, introInstance={(introInstance != null ? introInstance.name : "null")}, needShow={needShow}, active={(introInstance != null && introInstance.gameObject.activeSelf)}, time={Time.realtimeSinceStartup:F2}");
                lastIntroEligibility = introEligible;
            }

            if (!introEligible)
            {
                holdStartTime = -1f;
                isHolding = false;
                currentProgress = 0f;
                return;
            }

            if (!_skipIntroEventSubscribed && SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnSkipIntroReceived += OnRemoteSkipIntro;
                _skipIntroEventSubscribed = true;
                CoopMod.Logger.LogInfo("[IntroSkip] Subscribed to OnSkipIntroReceived event");
            }

            if (!iconReady && !iconAttempted)
            {
                CacheButtonIcon(introInstance);
            }

            bool mouseHeld = Input.GetMouseButton(0);
            bool rawJoystickA = Input.GetKey(KeyCode.JoystickButton0);
            bool submitButton = SafeGetButton("Submit");
            bool lazyInteractionHeld = SafeLazyGetKey(GameKey.Interaction);
            bool lazySelectHeld = SafeLazyGetKey(GameKey.Select);
            bool controllerHeld = rawJoystickA
                               || submitButton
                               || lazyInteractionHeld
                               || lazySelectHeld;

            if (mouseHeld || controllerHeld)
            {
                if (!isHolding)
                {
                    isHolding = true;
                    holdStartTime = Time.unscaledTime;
                    CoopMod.Logger.LogInfo($"[IntroSkipDebug] hold started source={(mouseHeld ? "mouse" : "controller")}, rawJoystickA={rawJoystickA}, submit={submitButton}, lazyInteraction={lazyInteractionHeld}, lazySelect={lazySelectHeld}, lazyGamepadActive={SafeLazyGamepadActive()}, time={Time.realtimeSinceStartup:F2}");
                }
                currentProgress = Mathf.Clamp01((Time.unscaledTime - holdStartTime) / HoldDuration);

                if (Time.realtimeSinceStartup - lastHoldDebugLogTime >= 0.75f)
                {
                    CoopMod.Logger.LogInfo($"[IntroSkipDebug] hold progress={currentProgress:P0}, rawJoystickA={rawJoystickA}, submit={submitButton}, lazyInteraction={lazyInteractionHeld}, lazySelect={lazySelectHeld}, mouse={mouseHeld}, time={Time.realtimeSinceStartup:F2}");
                    lastHoldDebugLogTime = Time.realtimeSinceStartup;
                }

                if (currentProgress >= 1f)
                {
                    SkipIntro();
                }
            }
            else
            {
                if (isHolding)
                {
                    CoopMod.Logger.LogInfo($"[IntroSkipDebug] hold cancelled at progress={currentProgress:P0}, time={Time.realtimeSinceStartup:F2}");
                }
                isHolding = false;
                holdStartTime = -1f;
                currentProgress = 0f;
            }
        }

        void OnGUI()
        {
            if (skipTriggered || currentProgress <= 0.001f) return;

            Intro introInstance = SkipIntroCutscene.GetIntroInstance();
            bool needShow = SkipIntroCutscene.NeedShowFirstIntro();
            if (introInstance == null || !needShow || !introInstance.gameObject.activeSelf)
                return;

            EnsureTextures();

            float scale = Mathf.Min(Screen.width, Screen.height) / 720f;
            float displaySize = TexSize * scale;
            bool usingController = IsControllerInput();

            float cx, cy;
            if (usingController)
            {
                cx = Screen.width - 80f * scale - displaySize / 2f;
                cy = Screen.height - 80f * scale - displaySize / 2f;
            }
            else
            {
                Vector2 mousePos = Input.mousePosition;
                cx = mousePos.x - displaySize / 2f;
                cy = Screen.height - mousePos.y - displaySize / 2f;
            }

            RegenerateRingTexture(currentProgress);
            GUI.DrawTexture(new Rect(cx, cy, displaySize, displaySize), ringTexture);

            DrawPromptLabel(cx, cy, displaySize, usingController, scale);
        }

        /// <summary>
        /// Extract the A-button icon sprite from the NGUI font's symbol table.
        /// The game uses BMSymbol (not BMGlyph) for multi-character sequences
        /// like "(A)", "(B)", "(LS)", etc. Each symbol maps to a sprite in the atlas.
        /// </summary>
        private void CacheButtonIcon(Intro introInstance)
        {
            iconAttempted = true;

            try
            {
                UIFont font = null;
                if (introInstance != null && introInstance.subtitle_text != null)
                    font = introInstance.subtitle_text.bitmapFont;
                if (font == null)
                {
                    var existingLabel = FindObjectOfType<UILabel>();
                    if (existingLabel != null) font = existingLabel.bitmapFont;
                }
                if (font == null)
                {
                    CoopMod.Logger.LogWarning("[IntroSkip] No UIFont found");
                    return;
                }

                // Use public methods: get_texture(), get_atlas(), get_symbols()
                // These were confirmed in the log dump.
                Texture2D tex = font.GetType().GetProperty("texture")?.GetValue(font, null) as Texture2D;
                if (tex == null)
                {
                    CoopMod.Logger.LogWarning("[IntroSkip] Cannot get font texture");
                    return;
                }
                CoopMod.Logger.LogInfo($"[IntroSkip] Font texture: {tex.width}x{tex.height}");

                // Get the atlas (for sprite UVs)
                UIAtlas atlas = font.GetType().GetProperty("atlas")?.GetValue(font, null) as UIAtlas;
                if (atlas == null)
                {
                    CoopMod.Logger.LogWarning("[IntroSkip] Cannot get font atlas");
                    return;
                }

                // Get the BMSymbol for "(A)"
                // The public method is: BMSymbol GetSymbol(string text, bool create)
                var getSymbolMethod = typeof(UIFont).GetMethod("GetSymbol",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(string), typeof(bool) }, null);

                object symbolObj = null;
                if (getSymbolMethod != null)
                {
                    symbolObj = getSymbolMethod.Invoke(font, new object[] { "(A)", false });
                    CoopMod.Logger.LogInfo($"[IntroSkip] GetSymbol(\"(A)\") returned: {(symbolObj != null ? symbolObj.GetType().Name : "null")}");
                }

                // If GetSymbol didn't work, try scanning mSymbols list
                if (symbolObj == null)
                {
                    var symbolsField = typeof(UIFont).GetField("mSymbols",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (symbolsField != null)
                    {
                        var symbolsList = symbolsField.GetValue(font) as System.Collections.IList;
                        if (symbolsList != null && symbolsList.Count > 0)
                        {
                            CoopMod.Logger.LogInfo($"[IntroSkip] Scanning {symbolsList.Count} BMSymbols for \"(A)\"");
                            var seqField = symbolsList[0].GetType().GetField("sequence",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (seqField == null)
                                seqField = symbolsList[0].GetType().GetField("mSequence",
                                    BindingFlags.NonPublic | BindingFlags.Instance);

                            foreach (object sym in symbolsList)
                            {
                                string seq = seqField?.GetValue(sym) as string;
                                if (seq == "(A)")
                                {
                                    symbolObj = sym;
                                    CoopMod.Logger.LogInfo($"[IntroSkip] Found BMSymbol with sequence=\"(A)\"");
                                    break;
                                }
                            }
                        }
                    }
                }

                if (symbolObj == null)
                {
                    CoopMod.Logger.LogWarning("[IntroSkip] BMSymbol for \"(A)\" not found");
                    return;
                }

                // BMSymbol has a spriteName field. Get the sprite from the atlas.
                Type bmSymbolType = symbolObj.GetType();
                var spriteNameField = bmSymbolType.GetField("spriteName",
                    BindingFlags.Public | BindingFlags.Instance);
                if (spriteNameField == null)
                    spriteNameField = bmSymbolType.GetField("mSpriteName",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                string spriteName = spriteNameField?.GetValue(symbolObj) as string;
                CoopMod.Logger.LogInfo($"[IntroSkip] BMSymbol spriteName: \"{spriteName}\"");

                if (string.IsNullOrEmpty(spriteName))
                {
                    CoopMod.Logger.LogWarning("[IntroSkip] BMSymbol has no spriteName");
                    return;
                }

                // Get the UISpriteData from the atlas
                var getSpriteMethod = typeof(UIAtlas).GetMethod("GetSprite",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(string) }, null);

                object spriteData = null;
                if (getSpriteMethod != null)
                {
                    spriteData = getSpriteMethod.Invoke(atlas, new object[] { spriteName });
                }

                if (spriteData == null)
                {
                    // Try accessing the sprite list directly
                    var spritesField = typeof(UIAtlas).GetField("sprites",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                    if (spritesField == null)
                        spritesField = typeof(UIAtlas).GetField("mSprites",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                    if (spritesField != null)
                    {
                        var spritesList = spritesField.GetValue(atlas) as System.Collections.IList;
                        if (spritesList != null)
                        {
                            var nameField = spritesList[0]?.GetType().GetField("name",
                                BindingFlags.Public | BindingFlags.Instance);
                            foreach (object s in spritesList)
                            {
                                string n = nameField?.GetValue(s) as string;
                                if (n == spriteName) { spriteData = s; break; }
                            }
                        }
                    }
                }

                if (spriteData == null)
                {
                    CoopMod.Logger.LogWarning($"[IntroSkip] Sprite \"{spriteName}\" not found in atlas");
                    return;
                }

                // UISpriteData has fields: x, y, width, height, borderLeft, borderRight, etc.
                Type spriteDataType = spriteData.GetType();
                var sxField = spriteDataType.GetField("x", BindingFlags.Public | BindingFlags.Instance);
                var syField = spriteDataType.GetField("y", BindingFlags.Public | BindingFlags.Instance);
                var swField = spriteDataType.GetField("width", BindingFlags.Public | BindingFlags.Instance);
                var shField = spriteDataType.GetField("height", BindingFlags.Public | BindingFlags.Instance);

                int sx = sxField != null ? Convert.ToInt32(sxField.GetValue(spriteData)) : 0;
                int sy = syField != null ? Convert.ToInt32(syField.GetValue(spriteData)) : 0;
                int sw = swField != null ? Convert.ToInt32(swField.GetValue(spriteData)) : 1;
                int sh = shField != null ? Convert.ToInt32(shField.GetValue(spriteData)) : 1;

                CoopMod.Logger.LogInfo($"[IntroSkip] Sprite rect: x={sx} y={sy} w={sw} h={sh}");

                // Calculate UV rect (Unity-style: V from bottom)
                float tw = tex.width;
                float th = tex.height;
                float u = sx / tw;
                float v = 1f - (sy + sh) / th;
                float uvW = sw / tw;
                float uvH = sh / th;

                iconAtlasTexture = tex;
                iconUVRect = new Rect(u, v, uvW, uvH);
                iconReady = true;

                CoopMod.Logger.LogInfo($"[IntroSkip] Button icon cached! UV=({u:F3},{v:F3},{uvW:F3},{uvH:F3})");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[IntroSkip] Icon caching failed: {ex.Message}");
                iconReady = false;
            }
        }

        private void DrawPromptLabel(float ringCx, float ringCy, float displaySize, bool usingController, float scale)
        {
            string labelText;
            if (usingController)
                labelText = "Hold to Skip Intro";
            else
                labelText = "Hold to Skip Intro";

            if (usingController && Time.realtimeSinceStartup - lastIntroSkipDebugLogTime >= 1f)
            {
                CoopMod.Logger.LogInfo($"[IntroSkipDebug] OnGUI label, iconReady={iconReady}, time={Time.realtimeSinceStartup:F2}");
                lastIntroSkipDebugLogTime = Time.realtimeSinceStartup;
            }

            if (promptLabelStyle == null)
            {
                promptLabelStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleLeft,
                    wordWrap = false,
                    richText = false
                };
                promptLabelStyle.normal.textColor = new Color(1f, 0.85f, 0.55f, 0.9f);
            }

            int fontSize = Mathf.RoundToInt(14 * scale);
            promptLabelStyle.fontSize = fontSize;

            float iconHeight = fontSize * 1.6f;
            float iconWidth = 0f;
            if (iconReady)
            {
                float aspect = iconUVRect.width > 0.001f ? iconUVRect.height / iconUVRect.width : 1f;
                iconWidth = iconHeight / aspect;
                iconWidth += 4f * scale; // gap between icon and text
            }
            else if (usingController)
            {
                labelText = "(A) " + labelText;
                iconWidth = 0f;
            }

            float textWidth = promptLabelStyle.CalcSize(new GUIContent(labelText)).x;
            float totalWidth = iconWidth + textWidth;
            float textHeight = fontSize * 1.3f;

            float labelX = ringCx + displaySize / 2f - totalWidth / 2f;
            float labelY;
            if (usingController)
                labelY = ringCy - textHeight - 4f * scale;
            else
                labelY = ringCy + displaySize + 4f * scale;

            Color savedColor = promptLabelStyle.normal.textColor;
            Color shadowColor = new Color(0f, 0f, 0f, 0.6f);

            float textStartX = labelX + iconWidth;

            promptLabelStyle.normal.textColor = shadowColor;
            GUI.Label(new Rect(textStartX + 1f, labelY + 1f, textWidth, textHeight), labelText, promptLabelStyle);
            GUI.Label(new Rect(textStartX - 1f, labelY - 1f, textWidth, textHeight), labelText, promptLabelStyle);

            promptLabelStyle.normal.textColor = savedColor;
            GUI.Label(new Rect(textStartX, labelY, textWidth, textHeight), labelText, promptLabelStyle);

            if (iconReady && usingController)
            {
                float iconX = labelX;
                float iconY = labelY + (textHeight - iconHeight) / 2f;

                GUI.DrawTextureWithTexCoords(
                    new Rect(iconX, iconY, iconWidth - 4f * scale, iconHeight),
                    iconAtlasTexture,
                    iconUVRect);
            }
        }

        private bool IsControllerInput()
        {
            if (Input.GetKey(KeyCode.JoystickButton0) || SafeGetButton("Submit") || SafeLazyGetKey(GameKey.Interaction) || SafeLazyGetKey(GameKey.Select))
                return true;
            try { if (Input.GetAxis("Submit") > 0.5f) return true; }
            catch { }
            return false;
        }

        private static bool SafeGetButton(string buttonName)
        {
            try { return Input.GetButton(buttonName); }
            catch { return false; }
        }

        private static bool SafeLazyGetKey(GameKey key)
        {
            try { return LazyInput.GetKey(key); }
            catch { return false; }
        }

        private static bool SafeLazyGamepadActive()
        {
            try { return LazyInput.gamepad_active; }
            catch { return false; }
        }

        private void EnsureTextures()
        {
            if (texturesInitialized) return;
            ringTexture = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            texturesInitialized = true;
        }

        private void RegenerateRingTexture(float progress)
        {
            if (ringTexture == null) return;

            Color32[] pixels = new Color32[TexSize * TexSize];
            float center = TexSize / 2f;
            float fillAngle = progress * Mathf.PI * 2f;
            float startAngle = Mathf.PI / 2f;
            Color ringFill = new Color(1f, 0.85f, 0.55f, 0.9f);

            for (int y = 0; y < TexSize; y++)
            {
                for (int x = 0; x < TexSize; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    int idx = y * TexSize + x;

                    if (dist < InnerRadius - 1f || dist > OuterRadius + 1f)
                    {
                        pixels[idx] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    float angle = Mathf.Atan2(-dy, dx);
                    if (angle < 0) angle += Mathf.PI * 2f;
                    float relAngle = startAngle - angle;
                    if (relAngle < 0) relAngle += Mathf.PI * 2f;

                    float edgeFade = 1f;
                    if (dist < InnerRadius) edgeFade = dist - InnerRadius + 1f;
                    else if (dist > OuterRadius) edgeFade = OuterRadius - dist + 1f;
                    edgeFade = Mathf.Clamp01(edgeFade);

                    if (relAngle <= fillAngle)
                    {
                        Color c = ringFill;
                        c.a *= edgeFade;
                        pixels[idx] = c;
                    }
                    else
                    {
                        Color bg = new Color(1f, 1f, 1f, 0.12f * edgeFade);
                        pixels[idx] = bg;
                    }
                }
            }

            ringTexture.SetPixels32(pixels);
            ringTexture.Apply();
        }

        void OnDestroy()
        {
            if (_skipIntroEventSubscribed && SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnSkipIntroReceived -= OnRemoteSkipIntro;
                _skipIntroEventSubscribed = false;
            }
            if (ringTexture != null)
            {
                Destroy(ringTexture);
                ringTexture = null;
            }
            iconAtlasTexture = null;
            iconReady = false;
        }

        private void SkipIntro()
        {
            if (skipTriggered) return;
            skipTriggered = true;
            currentProgress = 1f;

            // Multiplayer: broadcast skip to other players
            if (SteamLobbyManager.Instance?.IsInLobby == true)
            {
                SteamP2PManager.Instance?.SendSkipIntro();
            }

            CoopMod.Logger.LogInfo("[IntroSkip] Hold-to-skip triggered - skipping intro cutscene");
            Intro.OnIntroAnimationFinished();
        }

        private void OnRemoteSkipIntro(CSteamID senderID)
        {
            if (skipTriggered) return;
            // Don't skip if we're not in the intro
            Intro introInstance = SkipIntroCutscene.GetIntroInstance();
            bool needShow = SkipIntroCutscene.NeedShowFirstIntro();
            if (introInstance == null || !needShow || !introInstance.gameObject.activeSelf)
                return;

            CoopMod.Logger.LogInfo($"[IntroSkip] Remote player {senderID} skipped intro - skipping locally");
            SkipIntro();
        }
    }
}

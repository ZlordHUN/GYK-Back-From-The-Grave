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
        private const float RequestRetryInterval = 0.5f;
        private const float ProgressBroadcastInterval = 0.1f;
        private const float OwnerTimeout = 1.5f;
        private const int TexSize = 32;
        private const float OuterRadius = 14f;
        private const float InnerRadius = 10f;

        private float holdStartTime = -1f;
        private bool isHolding;
        private bool skipTriggered;
        private float currentProgress;
        private bool awaitingGrant;
        private bool localInputBlockedUntilRelease;
        private uint nextAttemptID;
        private uint localAttemptID;
        private float nextRequestAt;
        private float nextProgressBroadcastAt;

        private CSteamID activeSkipperID = CSteamID.Nil;
        private uint activeAttemptID;
        private float activeProgress;
        private float activeProgressUpdatedAt;
        private float ownerLastActivityAt;

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

        private SteamP2PManager subscribedP2PManager;

        void Awake()
        {
            EnsureNetworkSubscriptions();
        }

        public void ResetSkipState()
        {
            skipTriggered = false;
            isHolding = false;
            holdStartTime = -1f;
            currentProgress = 0f;
            awaitingGrant = false;
            localInputBlockedUntilRelease = false;
            localAttemptID = 0;
            nextRequestAt = 0f;
            nextProgressBroadcastAt = 0f;
            activeSkipperID = CSteamID.Nil;
            activeAttemptID = 0;
            activeProgress = 0f;
            activeProgressUpdatedAt = 0f;
            ownerLastActivityAt = 0f;
            lastIntroEligibility = false;
            lastIntroSkipDebugLogTime = -100f;
            lastHoldDebugLogTime = -100f;
            CoopMod.Logger.LogInfo("[IntroSkip] Skip state reset for new intro playback");
        }

        void Update()
        {
            EnsureNetworkSubscriptions();
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
                awaitingGrant = false;
                localInputBlockedUntilRelease = false;
                activeSkipperID = CSteamID.Nil;
                activeAttemptID = 0;
                activeProgress = 0f;
                return;
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
            bool inputHeld = mouseHeld || controllerHeld;
            float now = Time.realtimeSinceStartup;

            var lobby = SteamLobbyManager.Instance;
            bool isMultiplayer = lobby != null && lobby.IsInLobby && SteamP2PManager.Instance != null;

            if (isMultiplayer && lobby.IsHost && activeSkipperID != CSteamID.Nil &&
                now - ownerLastActivityAt > OwnerTimeout)
            {
                ReleaseActiveAttempt("owner progress timed out");
            }

            if (!inputHeld)
                localInputBlockedUntilRelease = false;

            if (activeSkipperID != CSteamID.Nil && !IsLocalActiveSkipper())
            {
                if (inputHeld)
                    localInputBlockedUntilRelease = true;
                return;
            }

            if (awaitingGrant)
            {
                if (!inputHeld)
                {
                    CoopMod.Logger.LogInfo("[IntroSkip] Skip request cancelled before host grant");
                    SteamP2PManager.Instance?.SendIntroSkipRequest(localAttemptID, cancel: true);
                    awaitingGrant = false;
                    localAttemptID = 0;
                }
                else if (now >= nextRequestAt)
                {
                    SteamP2PManager.Instance?.SendIntroSkipRequest(localAttemptID, cancel: false);
                    nextRequestAt = now + RequestRetryInterval;
                }
                return;
            }

            // A client that released an already-granted hold waits for the host's Release message.
            if (activeSkipperID != CSteamID.Nil && IsLocalActiveSkipper() && !isHolding)
                return;

            if (inputHeld)
            {
                if (localInputBlockedUntilRelease)
                    return;

                if (!isHolding)
                {
                    BeginLocalAttempt(isMultiplayer, lobby);
                    if (!isHolding)
                        return;

                    CoopMod.Logger.LogInfo($"[IntroSkipDebug] hold started source={(mouseHeld ? "mouse" : "controller")}, rawJoystickA={rawJoystickA}, submit={submitButton}, lazyInteraction={lazyInteractionHeld}, lazySelect={lazySelectHeld}, lazyGamepadActive={SafeLazyGamepadActive()}, time={Time.realtimeSinceStartup:F2}");
                }
                currentProgress = Mathf.Clamp01((Time.unscaledTime - holdStartTime) / HoldDuration);

                if (isMultiplayer && IsLocalActiveSkipper())
                {
                    if (lobby.IsHost)
                        ownerLastActivityAt = now;

                    if (now >= nextProgressBroadcastAt)
                    {
                        SteamP2PManager.Instance?.BroadcastIntroSkipProgress(localAttemptID, currentProgress);
                        nextProgressBroadcastAt = now + ProgressBroadcastInterval;
                    }
                }

                if (now - lastHoldDebugLogTime >= 0.75f)
                {
                    CoopMod.Logger.LogInfo($"[IntroSkipDebug] hold progress={currentProgress:P0}, rawJoystickA={rawJoystickA}, submit={submitButton}, lazyInteraction={lazyInteractionHeld}, lazySelect={lazySelectHeld}, mouse={mouseHeld}, time={Time.realtimeSinceStartup:F2}");
                    lastHoldDebugLogTime = now;
                }

                if (currentProgress >= 1f)
                {
                    if (isMultiplayer)
                        SteamP2PManager.Instance?.BroadcastIntroSkipProgress(localAttemptID, 1f);
                    SkipIntro(broadcast: true);
                }
            }
            else
            {
                if (isHolding)
                {
                    CoopMod.Logger.LogInfo($"[IntroSkipDebug] hold cancelled at progress={currentProgress:P0}, time={Time.realtimeSinceStartup:F2}");
                }
                CancelLocalHold(isMultiplayer, lobby);
            }
        }

        void OnGUI()
        {
            bool showingRemoteSkipper = activeSkipperID != CSteamID.Nil && !IsLocalActiveSkipper();
            float displayProgress = showingRemoteSkipper ? GetRemoteDisplayProgress() : currentProgress;
            if (skipTriggered || (!showingRemoteSkipper && displayProgress <= 0.001f)) return;

            Intro introInstance = SkipIntroCutscene.GetIntroInstance();
            bool needShow = SkipIntroCutscene.NeedShowFirstIntro();
            if (introInstance == null || !needShow || !introInstance.gameObject.activeSelf)
                return;

            EnsureTextures();

            float scale = Mathf.Min(Screen.width, Screen.height) / 720f;
            float displaySize = TexSize * scale;
            bool usingController = IsControllerInput();
            bool fixedPosition = showingRemoteSkipper || usingController;

            float cx, cy;
            if (fixedPosition)
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

            RegenerateRingTexture(displayProgress);
            GUI.DrawTexture(new Rect(cx, cy, displaySize, displaySize), ringTexture);

            string labelText = showingRemoteSkipper
                ? $"{GetPlayerName(activeSkipperID)} is skipping intro"
                : "Hold to Skip Intro";
            DrawPromptLabel(
                cx,
                cy,
                displaySize,
                fixedPosition,
                usingController && !showingRemoteSkipper,
                scale,
                labelText);
        }

        private void EnsureNetworkSubscriptions()
        {
            var manager = SteamP2PManager.Instance;
            if (ReferenceEquals(manager, subscribedP2PManager))
                return;

            if (subscribedP2PManager != null)
            {
                subscribedP2PManager.OnSkipIntroReceived -= OnRemoteSkipIntro;
                subscribedP2PManager.OnIntroSkipStateReceived -= OnIntroSkipStateReceived;
            }

            subscribedP2PManager = manager;
            if (subscribedP2PManager != null)
            {
                subscribedP2PManager.OnSkipIntroReceived += OnRemoteSkipIntro;
                subscribedP2PManager.OnIntroSkipStateReceived += OnIntroSkipStateReceived;
                CoopMod.Logger.LogInfo("[IntroSkip] Subscribed to intro skip network events");
            }
        }

        private void BeginLocalAttempt(bool isMultiplayer, SteamLobbyManager lobby)
        {
            if (!isMultiplayer)
            {
                StartLocalHold();
                return;
            }

            localAttemptID = NextAttemptID();
            CSteamID localID = SteamUser.GetSteamID();
            if (lobby.IsHost)
            {
                GrantAttempt(localID, localAttemptID);
                return;
            }

            awaitingGrant = true;
            nextRequestAt = Time.realtimeSinceStartup + RequestRetryInterval;
            SteamP2PManager.Instance.SendIntroSkipRequest(localAttemptID, cancel: false);
            CoopMod.Logger.LogInfo($"[IntroSkip] Requested skip ownership (attempt {localAttemptID})");
        }

        private uint NextAttemptID()
        {
            nextAttemptID++;
            if (nextAttemptID == 0)
                nextAttemptID++;
            return nextAttemptID;
        }

        private void StartLocalHold()
        {
            isHolding = true;
            holdStartTime = Time.unscaledTime;
            currentProgress = 0f;
            nextProgressBroadcastAt = 0f;
        }

        private void CancelLocalHold(bool isMultiplayer, SteamLobbyManager lobby)
        {
            if (!isHolding)
            {
                holdStartTime = -1f;
                currentProgress = 0f;
                return;
            }

            isHolding = false;
            holdStartTime = -1f;
            currentProgress = 0f;

            if (!isMultiplayer || activeSkipperID == CSteamID.Nil || !IsLocalActiveSkipper())
                return;

            if (lobby.IsHost)
            {
                ReleaseActiveAttempt("host cancelled hold");
            }
            else
            {
                SteamP2PManager.Instance?.SendIntroSkipRequest(localAttemptID, cancel: true);
                localInputBlockedUntilRelease = true;
            }
        }

        private void GrantAttempt(CSteamID ownerID, uint attemptID)
        {
            ApplyGrant(ownerID, attemptID);
            SteamP2PManager.Instance?.BroadcastIntroSkipAuthority(IntroSkipPhase.Grant, ownerID, attemptID);
            CoopMod.Logger.LogInfo($"[IntroSkip] Host granted skip ownership to {GetPlayerName(ownerID)} (attempt {attemptID})");
        }

        private void ApplyGrant(CSteamID ownerID, uint attemptID)
        {
            float now = Time.realtimeSinceStartup;
            if (activeSkipperID == ownerID && activeAttemptID == attemptID)
            {
                ownerLastActivityAt = now;
                return;
            }

            bool localOwnsGrant = ownerID == SteamUser.GetSteamID();
            activeSkipperID = ownerID;
            activeAttemptID = attemptID;
            activeProgress = 0f;
            activeProgressUpdatedAt = now;
            ownerLastActivityAt = now;

            if (localOwnsGrant)
            {
                awaitingGrant = false;
                localAttemptID = attemptID;
                StartLocalHold();
            }
            else
            {
                awaitingGrant = false;
                localAttemptID = 0;
                isHolding = false;
                holdStartTime = -1f;
                currentProgress = 0f;
                localInputBlockedUntilRelease = true;
            }
        }

        private void ReleaseActiveAttempt(string reason)
        {
            var lobby = SteamLobbyManager.Instance;
            if (lobby == null || !lobby.IsHost || activeSkipperID == CSteamID.Nil)
                return;

            CSteamID releasedOwner = activeSkipperID;
            uint releasedAttempt = activeAttemptID;
            ApplyRelease(releasedOwner, releasedAttempt);
            SteamP2PManager.Instance?.BroadcastIntroSkipAuthority(
                IntroSkipPhase.Release,
                releasedOwner,
                releasedAttempt);
            CoopMod.Logger.LogInfo($"[IntroSkip] Host released {GetPlayerName(releasedOwner)}'s skip attempt ({reason})");
        }

        private void ApplyRelease(CSteamID ownerID, uint attemptID)
        {
            if (activeSkipperID != ownerID || activeAttemptID != attemptID)
                return;

            bool releasedLocalOwner = IsLocalActiveSkipper();
            activeSkipperID = CSteamID.Nil;
            activeAttemptID = 0;
            activeProgress = 0f;
            activeProgressUpdatedAt = 0f;
            ownerLastActivityAt = 0f;

            if (releasedLocalOwner)
            {
                awaitingGrant = false;
                localAttemptID = 0;
                isHolding = false;
                holdStartTime = -1f;
                currentProgress = 0f;
            }
            else
            {
                localInputBlockedUntilRelease = true;
            }
        }

        private void OnIntroSkipStateReceived(
            CSteamID senderID,
            IntroSkipPhase phase,
            CSteamID ownerID,
            uint attemptID,
            float progress)
        {
            if (!IsIntroEligible())
                return;

            var lobby = SteamLobbyManager.Instance;
            if (lobby == null || !lobby.IsInLobby)
                return;

            switch (phase)
            {
                case IntroSkipPhase.Request:
                    if (!lobby.IsHost || senderID != ownerID)
                        return;

                    if (activeSkipperID == CSteamID.Nil)
                    {
                        GrantAttempt(ownerID, attemptID);
                    }
                    else
                    {
                        if (activeSkipperID == ownerID && activeAttemptID == attemptID)
                            ownerLastActivityAt = Time.realtimeSinceStartup;

                        // Re-announce the current owner so simultaneous or retried requests converge.
                        SteamP2PManager.Instance?.BroadcastIntroSkipAuthority(
                            IntroSkipPhase.Grant,
                            activeSkipperID,
                            activeAttemptID);
                    }
                    break;

                case IntroSkipPhase.Cancel:
                    if (!lobby.IsHost || senderID != ownerID)
                        return;
                    if (activeSkipperID == ownerID && activeAttemptID == attemptID)
                        ReleaseActiveAttempt("owner cancelled hold");
                    break;

                case IntroSkipPhase.Grant:
                    if (senderID != lobby.GetLobbyOwner())
                        return;
                    ApplyGrant(ownerID, attemptID);
                    break;

                case IntroSkipPhase.Release:
                    if (senderID != lobby.GetLobbyOwner())
                        return;
                    ApplyRelease(ownerID, attemptID);
                    break;

                case IntroSkipPhase.Progress:
                    if (senderID != ownerID || activeSkipperID != ownerID || activeAttemptID != attemptID)
                        return;

                    float progressReceivedAt = Time.realtimeSinceStartup;
                    if (progress >= activeProgress)
                    {
                        activeProgress = progress;
                        activeProgressUpdatedAt = progressReceivedAt;
                    }
                    if (lobby.IsHost)
                        ownerLastActivityAt = progressReceivedAt;
                    break;
            }
        }

        private bool IsLocalActiveSkipper()
        {
            return activeSkipperID != CSteamID.Nil && activeSkipperID == SteamUser.GetSteamID();
        }

        private static bool IsIntroEligible()
        {
            Intro introInstance = SkipIntroCutscene.GetIntroInstance();
            return introInstance != null &&
                   SkipIntroCutscene.NeedShowFirstIntro() &&
                   introInstance.gameObject.activeSelf;
        }

        private float GetRemoteDisplayProgress()
        {
            if (activeSkipperID == CSteamID.Nil)
                return 0f;

            float elapsed = Mathf.Max(0f, Time.realtimeSinceStartup - activeProgressUpdatedAt);
            return Mathf.Clamp01(activeProgress + elapsed / HoldDuration);
        }

        private static string GetPlayerName(CSteamID playerID)
        {
            string playerName = SteamFriends.GetFriendPersonaName(playerID);
            if (string.IsNullOrEmpty(playerName))
                return "Player";
            playerName = playerName.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return string.IsNullOrEmpty(playerName) ? "Player" : playerName;
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

        private void DrawPromptLabel(
            float ringCx,
            float ringCy,
            float displaySize,
            bool fixedPosition,
            bool showControllerIcon,
            float scale,
            string labelText)
        {
            if (showControllerIcon && Time.realtimeSinceStartup - lastIntroSkipDebugLogTime >= 1f)
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
            if (iconReady && showControllerIcon)
            {
                float aspect = iconUVRect.width > 0.001f ? iconUVRect.height / iconUVRect.width : 1f;
                iconWidth = iconHeight / aspect;
                iconWidth += 4f * scale; // gap between icon and text
            }
            else if (showControllerIcon)
            {
                labelText = "(A) " + labelText;
                iconWidth = 0f;
            }

            float textWidth = promptLabelStyle.CalcSize(new GUIContent(labelText)).x;
            float totalWidth = iconWidth + textWidth;
            float textHeight = fontSize * 1.3f;

            float labelX = ringCx + displaySize / 2f - totalWidth / 2f;
            labelX = Mathf.Clamp(labelX, 8f * scale, Mathf.Max(8f * scale, Screen.width - totalWidth - 8f * scale));
            float labelY;
            if (fixedPosition)
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

            if (iconReady && showControllerIcon)
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
            if (subscribedP2PManager != null)
            {
                subscribedP2PManager.OnSkipIntroReceived -= OnRemoteSkipIntro;
                subscribedP2PManager.OnIntroSkipStateReceived -= OnIntroSkipStateReceived;
                subscribedP2PManager = null;
            }
            if (ringTexture != null)
            {
                Destroy(ringTexture);
                ringTexture = null;
            }
            iconAtlasTexture = null;
            iconReady = false;
        }

        private void SkipIntro(bool broadcast)
        {
            if (skipTriggered) return;
            skipTriggered = true;
            currentProgress = 1f;

            // Multiplayer: broadcast skip to other players
            if (broadcast && SteamLobbyManager.Instance?.IsInLobby == true)
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

            CoopMod.Logger.LogInfo($"[IntroSkip] Remote player {GetPlayerName(senderID)} skipped intro - skipping locally");
            SkipIntro(broadcast: false);
        }
    }
}

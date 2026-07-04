using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Steamworks;
using GraveyardKeeperCoop.Network;
using GraveyardKeeperCoop.Utils;
using GraveyardKeeperCoop.UI;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Synchronizes game loading and cutscene start between host and client.
    /// Ensures both players are fully loaded before gameplay begins.
    /// This works by blocking the intro animation completion until all players are ready.
    /// </summary>
    public class GameLoadSync : MonoBehaviour
    {
        private const float PostLoadWarmupTimeoutSeconds = 30f;
        private const float PostLoadWarmupSettleSeconds = 1.5f;
        private const float LoadingIndicatorIntervalSeconds = 0.35f;
        private const float LoadingStatusBelowBarOffset = 34f;
        private const float LoadingStatusFallbackYRatio = -0.24f;

        private static GameLoadSync _instance;
        public static GameLoadSync Instance => _instance;

        // When true, LoadingGuiSyncPatch lets LoadingGUI.Hide run normally.
        // Set to true once sync actually finishes so we can dismiss the bar.
        public static bool AllowLoadingHide;

        // Sync state
        private bool isWaitingForSync;
        private bool localPlayerReady;
        private bool introBlocked;
        private HashSet<CSteamID> readyPlayers = new HashSet<CSteamID>();
        private int expectedPlayerCount;

        // Pending intro callback - stored when intro finishes but we're still waiting for sync
        private Action pendingIntroCallback;

        // Callbacks stashed by LoadingGuiSyncPatch while we were blocking
        // LoadingGUI.Hide. Invoked once after we drop our overlay so the base
        // game's post-load flow still runs.
        private readonly List<GJCommons.VoidDelegate> pendingHideCallbacks = new List<GJCommons.VoidDelegate>();

        // UI Elements
        private GameObject syncOverlay;
        private UIRoot syncUiRoot;
        private UISprite syncBackground;
        private UILabel statusLabel;
        private UILabel nativeLoadingLabel;
        private string statusBaseText = "Synchronizing";
        private float nextStatusRenderTime;
        private int loadingIndicatorStep;

        // Once the base game's native load finishes and tries to Hide, our
        // patch blocks the call and we take ownership of the loading bar.
        // The base game filled it 0→1 during its own load; we then drop it
        // back to 0.5 ("game prep done, sync starting") and animate it up
        // toward 0.95 as sync runs. The final 0.95→1.0 jump happens only
        // when sync actually completes — that way the bar never lies about
        // being done.
        private bool ownsLoadingBar;
        private float currentProgressBarValue = -1f;
        private float syncBarStartedAt = -1f;

        // Expected duration of the sync phase, used for the ease-out curve
        // that animates the bar from 0.5 → 0.95. Picked generously so the bar
        // keeps moving during long syncs without ever reaching 1.0 on its own.
        private const float SyncBarAnimationSeconds = 12f;
        private const float SyncBarStartValue = 0.5f;
        private const float SyncBarCruiseValue = 0.95f;
        
        // Events
        public static event Action OnAllPlayersReady;
        
        // Track if we've already synced for this game session
        private bool hasSyncedThisSession;

        // Keep the loading overlay up briefly after both peers report ready so heavy
        // initial sync scans happen before players regain control.
        private bool postLoadWarmupInProgress;
        
        // Track if we're in the intro cutscene phase (from ShowIntro until we leave sync)
        private bool isInIntroPhase;
        
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            
            _instance = this;
            DontDestroyOnLoad(gameObject);
            
            CoopMod.Logger.LogInfo("[GameLoadSync] Initialized");
        }
        
        private void OnEnable()
        {
            // Subscribe to P2P events
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnPlayerReadyToPlay -= OnRemotePlayerReady;
                SteamP2PManager.Instance.OnPlayerReadyToPlay += OnRemotePlayerReady;
            }
        }
        
        private void OnDisable()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnPlayerReadyToPlay -= OnRemotePlayerReady;
            }
        }

        private void Update()
        {
            if (!isWaitingForSync)
                return;

            // Lazy (re)capture of the native loading bar's UILabel. The base
            // game constructs LoadingGUI lazily during its first Show() call,
            // which often runs AFTER StartWaitingForPlayers. Retry every tick
            // until we have it.
            if (nativeLoadingLabel == null)
                CaptureNativeLoadingLabel();

            if (syncOverlay == null || !syncOverlay.activeSelf)
            {
                // Even when our fallback overlay is hidden, we still want to push
                // status text to the native loading bar if it's currently up.
                if (nativeLoadingLabel == null)
                    return;
            }

            UpdateOverlayBackground();
            PositionStatusLabelUnderLoadingBar();
            UpdateProgressBar();

            if (Time.realtimeSinceStartup < nextStatusRenderTime)
                return;

            loadingIndicatorStep = (loadingIndicatorStep + 1) % 4;
            RenderStatusText();
            nextStatusRenderTime = Time.realtimeSinceStartup + LoadingIndicatorIntervalSeconds;
        }

        /// <summary>
        /// Drive the native loading bar's fill to reflect sync progress.
        /// Phases:
        ///   • Base game load: 0.0 → 1.0 (driven by the base game; we don't touch)
        ///   • Native load done, sync starting: drop to 0.5
        ///   • Sync running: animate 0.5 → 0.95 over ~12s (ease-out)
        ///   • Sync done (FinishStartingGameplay): jump to 1.0, then fade out
        /// The bar approaches but never reaches 1.0 on its own — only the
        /// actual completion of sync triggers the final 0.95 → 1.0 jump, so
        /// the bar never lies about being done.
        /// </summary>
        private void UpdateProgressBar()
        {
            if (!ownsLoadingBar || syncBarStartedAt < 0f)
                return;

            float elapsed = Time.realtimeSinceStartup - syncBarStartedAt;
            float t = Mathf.Clamp01(elapsed / SyncBarAnimationSeconds);
            // Ease-out quadratic: fast progress early, slow approach to cruise.
            t = 1f - (1f - t) * (1f - t);
            float target = SyncBarStartValue + (SyncBarCruiseValue - SyncBarStartValue) * t;

            if (Mathf.Abs(currentProgressBarValue - target) < 0.005f)
                return;

            currentProgressBarValue = target;
            try
            {
                LoadingGUI.SetProgressBar(target);
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[GameLoadSync] Failed to set progress bar: {ex.Message}");
            }
        }

        /// <summary>
        /// Grab the UILabel that the native LoadingGUI uses for its "Loading"
        /// text. We rewrite its text in RenderStatusText so the loading bar
        /// shows sync status instead of the localized "Loading" string.
        /// </summary>
        private void CaptureNativeLoadingLabel()
        {
            try
            {
                LoadingGUI loading = GUIElements.me?.loading;
                if (loading == null)
                {
                    nativeLoadingLabel = null;
                    return;
                }

                // LocalizedLabel sits on the same GameObject as the UILabel it
                // controls — the LocalizedLabel.Localize() call writes the
                // localized "Loading" text into that UILabel.
                LocalizedLabel localized = loading.GetComponentInChildren<LocalizedLabel>(true);
                if (localized != null)
                {
                    nativeLoadingLabel = localized.GetComponent<UILabel>();
                    CoopMod.Logger.LogInfo("[GameLoadSync] Captured native loading label for sync status");
                }
                else
                {
                    nativeLoadingLabel = loading.GetComponentInChildren<UILabel>(true);
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[GameLoadSync] Failed to capture native loading label: {ex.Message}");
                nativeLoadingLabel = null;
            }
        }

        private void RestoreNativeLoadingLabel()
        {
            if (nativeLoadingLabel == null)
                return;

            try
            {
                // Re-run localization so the label returns to its proper "Loading"
                // string (in case our text is still showing when the bar hides).
                LocalizedLabel localized = nativeLoadingLabel.GetComponent<LocalizedLabel>();
                if (localized != null)
                    localized.Localize();
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[GameLoadSync] Failed to restore native loading label: {ex.Message}");
            }

            nativeLoadingLabel = null;
        }
        
        /// <summary>
        /// Start waiting for all players to finish loading.
        /// Called when game starts loading (either host starting or client receiving save).
        /// </summary>
        public void StartWaitingForPlayers(int playerCount)
        {
            // IMPORTANT: Always allow sync to start - don't skip based on hasSyncedThisSession
            // The hasSyncedThisSession flag could be stale if players didn't return to main menu
            // We reset it here to ensure fresh sync state
            if (hasSyncedThisSession)
            {
                CoopMod.Logger.LogInfo("[GameLoadSync] Resetting hasSyncedThisSession flag for new game");
                hasSyncedThisSession = false;
            }

            CoopMod.Logger.LogInfo($"[GameLoadSync] Starting sync wait for {playerCount} players");

            isWaitingForSync = true;
            localPlayerReady = false;
            introBlocked = false;
            readyPlayers.Clear();
            expectedPlayerCount = playerCount;
            pendingIntroCallback = null;
            pendingHideCallbacks.Clear();
            AllowLoadingHide = false;
            ownsLoadingBar = false;
            currentProgressBarValue = -1f;
            syncBarStartedAt = -1f;

            // Pause game time immediately
            PauseGameTime();

            // Capture the native loading bar's UILabel so we can rewrite its
            // text with sync status (the localized "Loading" string otherwise
            // stays on screen for the whole sync window).
            CaptureNativeLoadingLabel();

            // Show the sync overlay (acts as a fallback dark backdrop + status
            // text only when the native loading bar isn't visible).
            CreateSyncOverlay();
            UpdateStatusText("Loading world");
        }
        
        /// <summary>
        /// Called when the local player has finished loading (player spawned, world ready).
        /// This is called from LocalCoopActivationPatch when the player spawns.
        /// </summary>
        public void NotifyLocalPlayerReady()
        {
            if (!isWaitingForSync)
            {
                CoopMod.Logger.LogInfo("[GameLoadSync] Not waiting for sync, skipping local ready notification");
                return;
            }
            
            if (localPlayerReady)
            {
                CoopMod.Logger.LogInfo("[GameLoadSync] Local player already marked as ready");
                return;
            }
            
            CoopMod.Logger.LogInfo("[GameLoadSync] Local player finished loading!");
            localPlayerReady = true;
            
            // Broadcast to all other players that we're ready
            SteamP2PManager.Instance?.BroadcastReadyToPlay();
            
            UpdateStatusText($"Waiting for players ({GetReadyCount()}/{expectedPlayerCount})");
            
            CheckAllPlayersReady();
        }
        
        /// <summary>
        /// Called when a remote player signals they're ready.
        /// </summary>
        private void OnRemotePlayerReady(CSteamID playerID)
        {
            string playerName = SteamFriends.GetFriendPersonaName(playerID);
            CoopMod.Logger.LogInfo($"[GameLoadSync] Remote player ready: {playerName} ({playerID})");
            
            if (!readyPlayers.Contains(playerID))
            {
                readyPlayers.Add(playerID);
            }
            
            UpdateStatusText($"Waiting for players ({GetReadyCount()}/{expectedPlayerCount})");
            
            CheckAllPlayersReady();
        }
        
        private int GetReadyCount()
        {
            int count = readyPlayers.Count;
            if (localPlayerReady) count++;
            return count;
        }
        
        private void CheckAllPlayersReady()
        {
            int readyCount = GetReadyCount();
            
            CoopMod.Logger.LogInfo($"[GameLoadSync] Checking ready status: {readyCount}/{expectedPlayerCount}");
            
            if (readyCount >= expectedPlayerCount)
            {
                CoopMod.Logger.LogInfo("[GameLoadSync] All players ready! Starting gameplay...");
                
                AllPlayersReady();
            }
        }
        
        /// <summary>
        /// Called when all players are ready to begin gameplay.
        /// </summary>
        private void AllPlayersReady()
        {
            isWaitingForSync = false;
            hasSyncedThisSession = true;

            if (postLoadWarmupInProgress)
                return;

            StartCoroutine(PostLoadWarmupThenStartGameplay());
        }

        private IEnumerator PostLoadWarmupThenStartGameplay()
        {
            postLoadWarmupInProgress = true;
            PauseGameTime();

            float startedAt = Time.realtimeSinceStartup;
            float readySince = -1f;
            string lastStatus = null;

            while (Time.realtimeSinceStartup - startedAt < PostLoadWarmupTimeoutSeconds)
            {
                bool ready = IsPostLoadWarmupReady(out string waitingFor);
                string status = ready ? "Starting game" : waitingFor;

                if (status != lastStatus)
                {
                    UpdateStatusText(status, log: false);
                    lastStatus = status;
                }

                if (ready)
                {
                    if (readySince < 0f)
                        readySince = Time.realtimeSinceStartup;

                    if (Time.realtimeSinceStartup - readySince >= PostLoadWarmupSettleSeconds)
                        break;
                }
                else
                {
                    readySince = -1f;
                }

                yield return null;
            }

            if (Time.realtimeSinceStartup - startedAt >= PostLoadWarmupTimeoutSeconds)
                CoopMod.Logger.LogWarning("[GameLoadSync] Post-load warmup timed out; starting gameplay anyway");

            postLoadWarmupInProgress = false;
            FinishStartingGameplay();
        }

        private bool IsPostLoadWarmupReady(out string waitingFor)
        {
            waitingFor = string.Empty;

            OnlineCoopManager onlineCoop = OnlineCoopManager.Instance;
            if (expectedPlayerCount > 1 && (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled))
            {
                waitingFor = "Starting online session";
                return false;
            }

            if (expectedPlayerCount > 1 && onlineCoop.RemotePlayerComponent == null)
            {
                waitingFor = "Spawning remote player";
                return false;
            }

            // NOTE: We deliberately do NOT wait for LiveWGOTransformSync's
            // initial cache build here. That cache only builds once
            // MainGame.game_started flips true (its Update guard requires a
            // started session), but game_started only flips AFTER this warmup
            // completes. Waiting on it was a deadlock that forced the warmup
            // to time out every time. The cache builds quickly in the
            // background once game_started is true.
            return true;
        }

        private void FinishStartingGameplay()
        {
            // Hide the sync overlay
            HideSyncOverlay();

            // Restore the native loading label text (in case we replaced it).
            RestoreNativeLoadingLabel();

            // Snap the bar from its cruise value (~0.95) to 1.0 so the player
            // sees the loading bar actually complete before it fades out.
            try
            {
                if (LoadingGUI.is_shown)
                    LoadingGUI.SetProgressBar(1f);
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[GameLoadSync] Failed to set progress bar to 1.0: {ex.Message}");
            }

            // Allow the native loading bar to hide now, then dismiss it
            // ourselves so the player isn't left staring at a "Loading" screen.
            AllowLoadingHide = true;
            ownsLoadingBar = false;
            currentProgressBarValue = -1f;
            syncBarStartedAt = -1f;
            try
            {
                if (LoadingGUI.is_shown)
                {
                    LoadingGUI.Hide(null);
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[GameLoadSync] Failed to hide native loading bar: {ex.Message}");
            }

            // Fire any callbacks the base game tried to run when it first tried
            // to hide the loading bar.
            FlushPendingHideCallbacks();

            // Resume game time
            ResumeGameTime();

            // NOTE: Remote player is now spawned immediately when local player spawns
            // (in LocalCoopActivationPatch), just like local coop. No need to spawn here.

            // Trigger event for other systems
            OnAllPlayersReady?.Invoke();

            ChatManager.AddMessage("[System] All players ready! Starting game...");

            // If the intro was blocked, now execute the pending callback
            if (introBlocked && pendingIntroCallback != null)
            {
                CoopMod.Logger.LogInfo("[GameLoadSync] Executing pending intro callback now that all players are ready");
                var callback = pendingIntroCallback;
                pendingIntroCallback = null;
                introBlocked = false;
                callback.Invoke();
            }
        }
        
        /// <summary>
        /// Spawn the remote player so they're visible during the intro cutscene.
        /// This is called when all players are synced, BEFORE the intro plays.
        /// </summary>
        private void SpawnRemotePlayerForIntro()
        {
            try
            {
                var lobbyManager = SteamLobbyManager.Instance;
                if (lobbyManager == null || !lobbyManager.IsInLobby)
                {
                    CoopMod.Logger.LogInfo("[GameLoadSync] Not in lobby, skipping remote player spawn");
                    return;
                }
                
                var onlineCoopManager = OnlineCoopManager.Instance;
                if (onlineCoopManager == null)
                {
                    CoopMod.Logger.LogWarning("[GameLoadSync] OnlineCoopManager not found!");
                    return;
                }
                
                if (onlineCoopManager.IsOnlineCoopEnabled)
                {
                    CoopMod.Logger.LogInfo("[GameLoadSync] Online coop already enabled, skipping");
                    return;
                }
                
                int memberCount = lobbyManager.GetLobbyMemberCount();
                if (memberCount <= 1)
                {
                    CoopMod.Logger.LogInfo("[GameLoadSync] Only 1 member in lobby, skipping remote player spawn");
                    return;
                }
                
                bool isHost = lobbyManager.IsHost;
                CoopMod.Logger.LogInfo($"[GameLoadSync] Spawning remote player for intro. IsHost: {isHost}");
                
                CSteamID myID = SteamUser.GetSteamID();
                
                if (isHost)
                {
                    // Find the first client
                    for (int i = 0; i < memberCount; i++)
                    {
                        CSteamID memberID = SteamMatchmaking.GetLobbyMemberByIndex(lobbyManager.CurrentLobbyID, i);
                        if (memberID != myID)
                        {
                            CoopMod.Logger.LogInfo($"[GameLoadSync] Host enabling online coop with client {memberID}");
                            onlineCoopManager.EnableAsHost(memberID);
                            break;
                        }
                    }
                }
                else
                {
                    // Get the host's ID
                    CSteamID hostID = SteamMatchmaking.GetLobbyOwner(lobbyManager.CurrentLobbyID);
                    CoopMod.Logger.LogInfo($"[GameLoadSync] Client enabling online coop with host {hostID}");
                    onlineCoopManager.EnableAsClient(hostID);
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[GameLoadSync] Error spawning remote player for intro: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Called from IntroSyncPatch when ShowIntro is reached.
        /// This is the sync point - marks player ready and stores the intro callback.
        /// </summary>
        public void NotifyReadyForIntro(Action introCallback)
        {
            // Mark that we're now in the intro phase (for dialogue sync AND position sync)
            isInIntroPhase = true;
            CoopMod.Logger.LogInfo("[GameLoadSync] NotifyReadyForIntro: IsInIntroPhase set to TRUE");
            
            if (!isWaitingForSync)
            {
                CoopMod.Logger.LogInfo("[GameLoadSync] Not waiting for sync, executing intro immediately");
                introCallback?.Invoke();
                return;
            }
            
            CoopMod.Logger.LogInfo("[GameLoadSync] Player reached intro point - marking ready");
            
            // Store the callback
            introBlocked = true;
            pendingIntroCallback = introCallback;
            
            // Now mark this player as ready
            if (!localPlayerReady)
            {
                localPlayerReady = true;
                
                // Broadcast to all other players that we're ready
                SteamP2PManager.Instance?.BroadcastReadyToPlay();
                
                UpdateStatusText($"Waiting for players ({GetReadyCount()}/{expectedPlayerCount})");
            }
            
            CheckAllPlayersReady();
        }
        
        /// <summary>
        /// Called from our patch on Intro.ShowIntro.
        /// Returns true if the intro should be delayed (waiting for sync).
        /// </summary>
        public bool ShouldDelayIntro()
        {
            if (!isWaitingForSync)
            {
                CoopMod.Logger.LogInfo("[GameLoadSync] Not waiting for sync, allowing intro to start");
                return false;
            }
            
            CoopMod.Logger.LogInfo("[GameLoadSync] Sync in progress - intro will be delayed until all players ready");
            return true;
        }
        
        /// <summary>
        /// Sets the callback to execute when all players are ready and intro should start
        /// </summary>
        public void SetIntroReadyCallback(Action callback)
        {
            CoopMod.Logger.LogInfo("[GameLoadSync] Intro ready callback registered");
            introBlocked = true;
            pendingIntroCallback = callback;
        }
        
        /// <summary>
        /// DEPRECATED: Called from our old patch on Intro.OnIntroAnimationFinished.
        /// Returns true if the intro should be blocked (waiting for sync).
        /// If blocked, stores the callback to execute later when sync completes.
        /// </summary>
        public bool ShouldBlockIntro(Action introCallback)
        {
            if (!isWaitingForSync)
            {
                CoopMod.Logger.LogInfo("[GameLoadSync] Not waiting for sync, allowing intro to proceed");
                return false;
            }
            
            CoopMod.Logger.LogInfo("[GameLoadSync] Blocking intro animation until all players ready");
            introBlocked = true;
            pendingIntroCallback = introCallback;
            
            // Update status to show we're waiting
            UpdateStatusText($"Waiting for players ({GetReadyCount()}/{expectedPlayerCount})");
            
            return true;
        }
        
        /// <summary>
        /// Pause game time while waiting for sync.
        /// </summary>
        public void PauseGameTime()
        {
            try
            {
                if (EnvironmentEngine.me != null)
                {
                    EnvironmentEngine.me.EnableTime(false);
                    CoopMod.Logger.LogInfo("[GameLoadSync] Game time paused for sync");
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[GameLoadSync] Could not pause game time: {ex.Message}");
            }
            
            // Only try to disable player control if the game has actually started
            // and the player is properly spawned
            // Check MainGame.game_started to avoid accessing uninitialized player
            try
            {
                if (MainGame.game_started && MainGame.me?.player_char != null && MainGame.me.player != null)
                {
                    MainGame.me.player_char.control_enabled = false;
                    CoopMod.Logger.LogInfo("[GameLoadSync] Player control disabled for sync");
                }
                else
                {
                    CoopMod.Logger.LogInfo("[GameLoadSync] Skipping player control disable (game not started or player not spawned)");
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[GameLoadSync] Could not disable player control: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Resume game time after sync.
        /// </summary>
        private void ResumeGameTime()
        {
            try
            {
                if (EnvironmentEngine.me != null)
                {
                    EnvironmentEngine.me.EnableTime(true);
                    CoopMod.Logger.LogInfo("[GameLoadSync] Game time resumed");
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[GameLoadSync] Could not resume game time: {ex.Message}");
            }
            
            // Only re-enable player control if game has started and player exists
            try
            {
                if (MainGame.game_started && MainGame.me?.player_char != null && MainGame.me.player != null)
                {
                    MainGame.me.player_char.control_enabled = true;
                    CoopMod.Logger.LogInfo("[GameLoadSync] Player control re-enabled");
                }
                else
                {
                    CoopMod.Logger.LogInfo("[GameLoadSync] Skipping player control re-enable (game not started or player not spawned)");
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[GameLoadSync] Could not re-enable player control: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Check if we're currently blocking the intro.
        /// </summary>
        public bool IsIntroBlocked => introBlocked;
        
        /// <summary>
        /// Create the sync overlay UI.
        /// </summary>
        private void CreateSyncOverlay()
        {
            if (syncOverlay != null)
            {
                syncOverlay.SetActive(true);
                return;
            }
            
            try
            {
                // Find the UI root
                UIRoot uiRoot = NGUITools.FindInParents<UIRoot>(GUIElements.me?.gameObject);
                if (uiRoot == null)
                {
                    uiRoot = UnityEngine.Object.FindObjectOfType<UIRoot>();
                }
                
                if (uiRoot == null)
                {
                    CoopMod.Logger.LogWarning("[GameLoadSync] Could not find UIRoot for sync overlay");
                    return;
                }
                
                // Create overlay container
                syncOverlay = new GameObject("GameLoadSyncOverlay");
                syncOverlay.transform.SetParent(uiRoot.transform, false);
                syncOverlay.layer = uiRoot.gameObject.layer;
                syncUiRoot = uiRoot;
                
                // Add a panel for rendering
                UIPanel panel = syncOverlay.AddComponent<UIPanel>();
                panel.depth = 1000; // On top of everything
                
                // Create a dark background
                GameObject bgObj = new GameObject("Background");
                bgObj.transform.SetParent(syncOverlay.transform, false);
                bgObj.layer = syncOverlay.layer;
                
                syncBackground = bgObj.AddComponent<UISprite>();
                syncBackground.atlas = NGUITools.FindInParents<UISprite>(GUIElements.me?.gameObject)?.atlas;
                syncBackground.spriteName = "pixel"; // Simple solid sprite
                syncBackground.color = new Color(0, 0, 0, 0.85f);
                syncBackground.width = Screen.width;
                syncBackground.height = Screen.height;
                syncBackground.depth = 0;
                
                // Create status label
                GameObject labelObj = new GameObject("StatusLabel");
                labelObj.transform.SetParent(syncOverlay.transform, false);
                labelObj.layer = syncOverlay.layer;
                
                statusLabel = labelObj.AddComponent<UILabel>();
                
                // Try to find a font from the game
                UILabel existingLabel = UnityEngine.Object.FindObjectOfType<UILabel>();
                if (existingLabel != null)
                {
                    statusLabel.trueTypeFont = existingLabel.trueTypeFont;
                    statusLabel.bitmapFont = existingLabel.bitmapFont;
                }
                
                statusLabel.fontSize = 28;
                statusLabel.color = Color.white;
                statusLabel.alignment = NGUIText.Alignment.Center;
                statusLabel.overflowMethod = UILabel.Overflow.ShrinkContent;
                statusLabel.width = 900;
                statusLabel.height = 44;
                statusLabel.depth = 1;
                statusLabel.text = "Synchronizing";
                PositionStatusLabelUnderLoadingBar();
                UpdateOverlayBackground();
                RenderStatusText();
                
                CoopMod.Logger.LogInfo("[GameLoadSync] Sync overlay created");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[GameLoadSync] Error creating sync overlay: {ex.Message}");
            }
        }
        
        private void UpdateStatusText(string text)
        {
            UpdateStatusText(text, log: true);
        }

        private void UpdateStatusText(string text, bool log)
        {
            statusBaseText = string.IsNullOrEmpty(text) ? "Synchronizing" : text;
            loadingIndicatorStep = 0;
            nextStatusRenderTime = Time.realtimeSinceStartup + LoadingIndicatorIntervalSeconds;
            RenderStatusText();

            if (log)
                CoopMod.Logger.LogInfo($"[GameLoadSync] Status: {statusBaseText}");
        }

        private void RenderStatusText()
        {
            string text = statusBaseText + new string('.', loadingIndicatorStep);

            if (statusLabel != null)
                statusLabel.text = text;

            // Push the same status to the native loading bar's label so players
            // see live sync progress on the actual loading screen (instead of
            // the static localized "Loading" string).
            if (nativeLoadingLabel != null)
                nativeLoadingLabel.text = text;
        }

        private void UpdateOverlayBackground()
        {
            if (syncBackground == null)
                return;

            bool nativeLoadingVisible = LoadingGUI.is_shown;
            Color color = syncBackground.color;
            color.a = nativeLoadingVisible ? 0f : 0.85f;
            syncBackground.color = color;
        }

        private void PositionStatusLabelUnderLoadingBar()
        {
            if (statusLabel == null || statusLabel.transform == null)
                return;

            Transform labelParent = statusLabel.transform.parent;
            if (labelParent == null)
                return;

            Vector3 position;
            if (!TryGetLoadingBarStatusPosition(labelParent, out position))
                position = GetFallbackLoadingStatusPosition();

            statusLabel.transform.localPosition = position;
        }

        private bool TryGetLoadingBarStatusPosition(Transform targetParent, out Vector3 localPosition)
        {
            localPosition = Vector3.zero;

            LoadingGUI loading = GUIElements.me?.loading;
            GameObject progressBar = loading?.progress_bar_go;
            if (progressBar == null || !LoadingGUI.is_shown)
                return false;

            UIWidget[] widgets = progressBar.GetComponentsInChildren<UIWidget>(true);
            if (widgets == null || widgets.Length == 0)
                return false;

            bool foundBounds = false;
            float minX = 0f;
            float maxX = 0f;
            float minY = 0f;

            for (int i = 0; i < widgets.Length; i++)
            {
                UIWidget widget = widgets[i];
                if (widget == null)
                    continue;

                Vector3[] corners = widget.worldCorners;
                if (corners == null || corners.Length < 4)
                    continue;

                for (int cornerIndex = 0; cornerIndex < 4; cornerIndex++)
                {
                    Vector3 localCorner = targetParent.InverseTransformPoint(corners[cornerIndex]);

                    if (!foundBounds)
                    {
                        minX = maxX = localCorner.x;
                        minY = localCorner.y;
                        foundBounds = true;
                    }
                    else
                    {
                        minX = Mathf.Min(minX, localCorner.x);
                        maxX = Mathf.Max(maxX, localCorner.x);
                        minY = Mathf.Min(minY, localCorner.y);
                    }
                }
            }

            if (!foundBounds)
                return false;

            localPosition = new Vector3((minX + maxX) * 0.5f, minY - LoadingStatusBelowBarOffset, 0f);
            return true;
        }

        private Vector3 GetFallbackLoadingStatusPosition()
        {
            UIRoot uiRoot = syncUiRoot ?? NGUITools.FindInParents<UIRoot>(GUIElements.me?.gameObject);
            if (uiRoot == null)
                uiRoot = UnityEngine.Object.FindObjectOfType<UIRoot>();
            int manualHeight = uiRoot != null ? uiRoot.manualHeight : 800;
            if (manualHeight <= 0)
                manualHeight = 800;

            return new Vector3(0f, manualHeight * LoadingStatusFallbackYRatio, 0f);
        }
        
        private void HideSyncOverlay()
        {
            if (syncOverlay != null)
            {
                syncOverlay.SetActive(false);
            }
        }

        /// <summary>Whether our fallback overlay is currently on screen.</summary>
        private bool IsOverlayVisible => syncOverlay != null && syncOverlay.activeSelf;
        
        /// <summary>
        /// Reset the sync state when returning to main menu.
        /// </summary>
        public void ResetSyncState()
        {
            isWaitingForSync = false;
            localPlayerReady = false;
            introBlocked = false;
            isInIntroPhase = false;
            readyPlayers.Clear();
            hasSyncedThisSession = false;
            postLoadWarmupInProgress = false;
            pendingIntroCallback = null;
            AllowLoadingHide = true;
            ownsLoadingBar = false;
            currentProgressBarValue = -1f;
            syncBarStartedAt = -1f;
            pendingHideCallbacks.Clear();
            RestoreNativeLoadingLabel();
            HideSyncOverlay();

            CoopMod.Logger.LogInfo("[GameLoadSync] Sync state reset");
        }

        /// <summary>
        /// Called by LoadingGuiSyncPatch when the base game tries to hide the
        /// loading bar while sync is still in progress. We hold onto the
        /// callback so FinishStartingGameplay can fire it after our overlay drops.
        /// </summary>
        public void StashPendingHideCallback(GJCommons.VoidDelegate callback)
        {
            // The base game only calls Hide after its own load completes, so
            // the first time we see this is the moment we transition from
            // "base game owns the loading bar" to "sync owns the loading bar".
            if (!ownsLoadingBar)
            {
                ownsLoadingBar = true;
                syncBarStartedAt = Time.realtimeSinceStartup;
                CoopMod.Logger.LogInfo("[GameLoadSync] Native load complete — taking ownership of loading bar for sync progress");
            }

            if (callback == null)
                return;

            pendingHideCallbacks.Add(callback);
        }

        private void FlushPendingHideCallbacks()
        {
            if (pendingHideCallbacks.Count == 0)
                return;

            // Snapshot then clear before invoking — a callback might re-enter Hide.
            var callbacks = pendingHideCallbacks.ToArray();
            pendingHideCallbacks.Clear();
            for (int i = 0; i < callbacks.Length; i++)
            {
                try { callbacks[i]?.TryInvoke(); }
                catch (System.Exception ex)
                {
                    CoopMod.Logger.LogWarning($"[GameLoadSync] Pending hide callback threw: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Check if we're currently waiting for sync.
        /// </summary>
        public bool IsWaitingForSync => isWaitingForSync;
        
        /// <summary>
        /// Check if local player is ready.
        /// </summary>
        public bool IsLocalPlayerReady => localPlayerReady;
        
        /// <summary>
        /// Check if we're in the intro cutscene phase (between ShowIntro and leaving sync).
        /// Used by DialogueSync to know if we should sync dialogue regardless of distance.
        /// Used by OnlineCoopManager to ENABLE position sync during intro (so remote player
        /// follows teleports and remains visible during the cutscene with the red-eyed ghost).
        /// </summary>
        public bool IsInIntroPhase => isInIntroPhase;
        
        /// <summary>
        /// Mark the start of the intro phase. Called when player spawns in online coop.
        /// During intro phase, position sync is ENABLED so remote player follows teleports
        /// and both players are visible together during the intro cutscene.
        /// </summary>
        public void StartIntroPhase()
        {
            isInIntroPhase = true;
            CoopMod.Logger.LogInfo("[GameLoadSync] Intro phase STARTED - IsInIntroPhase is now TRUE");
        }
        
        /// <summary>
        /// Mark the end of the intro phase. Called when the intro animation finishes.
        /// This allows position syncing to resume.
        /// </summary>
        public void EndIntroPhase()
        {
            isInIntroPhase = false;
            CoopMod.Logger.LogInfo("[GameLoadSync] Intro phase ENDED - IsInIntroPhase is now FALSE");
        }
    }
}

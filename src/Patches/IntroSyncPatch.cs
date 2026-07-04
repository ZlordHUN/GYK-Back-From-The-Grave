using HarmonyLib;
using GraveyardKeeperCoop.Multiplayer;
using GraveyardKeeperCoop.Network;
using System;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches the intro animation to synchronize game start between multiplayer players.
    /// This prevents the intro cutscene from STARTING until all players are loaded.
    /// Players stay on loading screen until sync is complete, then intro plays for everyone.
    /// 
    /// The remote player is teleported alongside the local player via TeleportToGDPoint patch
    /// when the "in the dark" scene starts - no need to freeze/sync walking.
    /// </summary>
    [HarmonyPatch]
    public class IntroSyncPatch
    {
        // Store the pending intro callback when we need to delay
        private static Action pendingIntroCallback;
        private static bool pendingNoWords;
        private static bool pendingStopAllPlaylist;
        private static bool introDelayed;
        
        // Re-entry guard to prevent infinite recursion when we call Intro.ShowIntro ourselves
        private static bool isCallingOriginal;
        
        /// <summary>
        /// Prefix patch on Intro.ShowIntro - this is where we sync both players
        /// When ShowIntro is called, it means the player has finished loading and is ready to see the intro
        /// </summary>
        [HarmonyPatch(typeof(Intro), "ShowIntro")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Normal)]
        public static bool ShowIntro_Prefix(Action on_finished, bool no_words, bool stop_all_playlist)
        {
            // Prevent re-entry when we call Intro.ShowIntro ourselves
            if (isCallingOriginal)
            {
                return true; // Let original method run
            }
            
            CoopMod.Logger.LogInfo("[IntroSyncPatch] ShowIntro_Prefix called!");
            
            // Check if we're in a multiplayer session
            bool isInLobby = SteamLobbyManager.Instance?.IsInLobby == true;
            CoopMod.Logger.LogInfo($"[IntroSyncPatch] IsInLobby: {isInLobby}");
            
            if (!isInLobby)
            {
                // Not in multiplayer, allow normal flow
                CoopMod.Logger.LogInfo("[IntroSyncPatch] Not in lobby, allowing normal intro");
                return true;
            }
            
            // Check if GameLoadSync exists
            if (GameLoadSync.Instance == null)
            {
                CoopMod.Logger.LogInfo("[IntroSyncPatch] GameLoadSync.Instance is null, allowing normal intro");
                return true;
            }
            
            // Check if sync is still in progress
            if (!GameLoadSync.Instance.IsWaitingForSync)
            {
                CoopMod.Logger.LogInfo("[IntroSyncPatch] Sync already complete, allowing intro with wrapped callback");
                
                Action wrappedCallback = () =>
                {
                    CoopMod.Logger.LogInfo("[IntroSyncPatch] Intro animation finished (sync path) - cutscene continues");
                    on_finished?.Invoke();
                };
                
                // Set re-entry guard and call original
                isCallingOriginal = true;
                try
                {
                    Intro.ShowIntro(wrappedCallback, no_words, stop_all_playlist);
                }
                finally
                {
                    isCallingOriginal = false;
                }
                return false; // We handled it
            }
            
            CoopMod.Logger.LogInfo("[IntroSyncPatch] Reached ShowIntro - marking player ready and waiting for others!");
            
            // Store the callback and parameters for later
            pendingIntroCallback = on_finished;
            pendingNoWords = no_words;
            pendingStopAllPlaylist = stop_all_playlist;
            introDelayed = true;
            
            // Mark this player as ready (they've reached the intro point)
            // The remote player will be teleported alongside when TeleportToGDPoint is called
            GameLoadSync.Instance.NotifyReadyForIntro(ExecutePendingIntro);
            
            // Skip the original ShowIntro - we'll call it later when all players are ready
            return false;
        }

        /// <summary>
        /// IntroSyncPatch is the sole Harmony owner of Intro.ShowIntro. The skip
        /// handler is UI/input behavior only; its per-playback state is reset here.
        /// </summary>
        [HarmonyPatch(typeof(Intro), "ShowIntro")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        public static void ShowIntro_Postfix()
        {
            SkipIntroCutscene.ResetSkipStateForCurrentIntro();
        }
        
        /// <summary>
        /// Called by GameLoadSync when all players are ready - starts the delayed intro
        /// </summary>
        public static void ExecutePendingIntro()
        {
            if (!introDelayed || pendingIntroCallback == null)
            {
                CoopMod.Logger.LogWarning("[IntroSyncPatch] ExecutePendingIntro called but no pending intro!");
                return;
            }
            
            CoopMod.Logger.LogInfo("[IntroSyncPatch] All players ready - executing delayed intro now!");
            
            // Store and clear the pending state
            Action originalCallback = pendingIntroCallback;
            bool noWords = pendingNoWords;
            bool stopPlaylist = pendingStopAllPlaylist;
            
            pendingIntroCallback = null;
            introDelayed = false;
            
            Action wrappedCallback = () =>
            {
                CoopMod.Logger.LogInfo("[IntroSyncPatch] Intro animation finished - cutscene continues with dark scene");
                originalCallback?.Invoke();
            };
            
            // Now call the original ShowIntro with our wrapped callback
            isCallingOriginal = true;
            try
            {
                Intro.ShowIntro(wrappedCallback, noWords, stopPlaylist);
            }
            finally
            {
                isCallingOriginal = false;
            }
        }
        
        /// <summary>
        /// Reset state when returning to main menu
        /// </summary>
        public static void ResetState()
        {
            pendingIntroCallback = null;
            introDelayed = false;
            CoopMod.Logger.LogInfo("[IntroSyncPatch] State reset");
        }
    }
}

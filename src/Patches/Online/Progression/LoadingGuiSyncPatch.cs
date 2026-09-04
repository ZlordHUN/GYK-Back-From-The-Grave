using GraveyardKeeperCoop.Multiplayer;
using HarmonyLib;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Keeps the native loading bar visible while GameLoadSync is waiting for
    /// all players to finish loading. Without this, the base game hides the
    /// loading bar the moment the local world finishes loading — leaving players
    /// staring at a dark screen (or the world) while sync continues behind the
    /// scenes. By blocking Hide until sync completes, the loading bar carries
    /// our sync-status text and progress instead.
    /// </summary>
    [HarmonyPatch(typeof(LoadingGUI))]
    public static class LoadingGuiSyncPatch
    {
        /// <summary>
        /// True only when GameLoadSync wants the native loading bar to stay
        /// up. The prefix blocks the base Hide implementation while this is set.
        /// </summary>
        public static bool BlockHide => GameLoadSync.Instance != null
                                        && GameLoadSync.Instance.IsWaitingForSync
                                        && !GameLoadSync.AllowLoadingHide;

        [HarmonyPatch("Show")]
        [HarmonyPrefix]
        public static bool Show_Prefix(
            GJCommons.VoidDelegate on_anim_played)
        {
            return GameLoadSync.Instance
                       ?.TryReuseTransferLoadingScreen(on_anim_played) != true;
        }

        [HarmonyPatch("Hide")]
        [HarmonyPrefix]
        public static bool Hide_Prefix(ref GJCommons.VoidDelegate on_anim_played)
        {
            if (!BlockHide)
                return true;

            // Stash the callback so GameLoadSync can fire it once sync actually finishes.
            GameLoadSync.Instance?.StashPendingHideCallback(on_anim_played);
            return false;
        }

        [HarmonyPatch("HideImmediate")]
        [HarmonyPrefix]
        public static bool HideImmediate_Prefix()
        {
            // HideImmediate() takes no callback (unlike Hide), so there is nothing
            // to stash — just block the base call while sync is holding the bar up.
            return !BlockHide;
        }
    }
}

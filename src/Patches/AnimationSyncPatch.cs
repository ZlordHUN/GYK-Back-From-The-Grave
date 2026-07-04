using HarmonyLib;
using BepInEx.Logging;
using GraveyardKeeperCoop.Multiplayer;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Syncs animation state changes (tool use, attack, etc.) to the remote player.
    /// Intercepts SetAnimationState on the local player and sends it via P2P.
    /// The remote side receives it in OnlineCoopManager.OnRemotePlayerAnimationReceived
    /// and applies it via BaseCharacterComponent.SetAnimationState + SetToolGraphics.
    /// </summary>
    [HarmonyPatch]
    public static class AnimationSyncPatch
    {
        private static ManualLogSource Logger => CoopMod.Logger;

        // Throttle to avoid flooding network during continuous tool use.
        // Walk/idle is driven by PlayerState packets; this reliable channel is for
        // tools/actions plus the idle transition that clears a synced action.
        private static CharAnimState lastSentAnimState = CharAnimState.Idle;
        private static ItemDefinition.ItemType lastSentItemType = ItemDefinition.ItemType.None;

        /// <summary>
        /// Intercepts SetAnimationState on the local player and sends it to the remote player.
        /// This fires whenever the animation state changes to Tool, Attack, Fishing, Idle, etc.
        /// </summary>
        [HarmonyPatch(typeof(BaseCharacterComponent), "SetAnimationState")]
        [HarmonyPostfix]
        public static void SetAnimationState_Postfix(BaseCharacterComponent __instance, CharAnimState state, ItemDefinition.ItemType item_type)
        {
            // Only sync for the LOCAL player
            if (__instance.wgo == null || __instance.wgo != MainGame.me?.player)
                return;

            // Only sync if online coop is active
            if (OnlineCoopManager.Instance == null || !OnlineCoopManager.Instance.IsOnlineCoopEnabled)
                return;

            bool isWalking = state == CharAnimState.Walking;
            bool isIdle = state == CharAnimState.Idle;
            bool lastSyncedWasAction = lastSentAnimState != CharAnimState.Idle &&
                                       lastSentAnimState != CharAnimState.Walking;

            if (isWalking)
                return;

            if (isIdle && !lastSyncedWasAction)
                return;

            // Only send if the state actually changed (avoid flooding during continuous tool use)
            if (state == lastSentAnimState && item_type == lastSentItemType)
                return;

            lastSentAnimState = state;
            lastSentItemType = item_type;

            Logger.LogDebug($"[AnimSync] Sending animation state: {state}, itemType={item_type}");
            SteamP2PManager.Instance?.SendAnimationState((int)state, (int)item_type);
        }

        /// <summary>
        /// Reset tracking when game ends or player changes
        /// </summary>
        public static void Reset()
        {
            lastSentAnimState = CharAnimState.Idle;
            lastSentItemType = ItemDefinition.ItemType.None;
        }

        internal static void SendToolStopped()
        {
            bool hadAction = lastSentAnimState != CharAnimState.Idle &&
                             lastSentAnimState != CharAnimState.Walking;
            lastSentAnimState = CharAnimState.Idle;
            lastSentItemType = ItemDefinition.ItemType.None;

            if (!hadAction)
                return;

            SteamP2PManager.Instance?.SendAnimationState(
                (int)CharAnimState.Idle,
                (int)ItemDefinition.ItemType.None);
        }
    }
}

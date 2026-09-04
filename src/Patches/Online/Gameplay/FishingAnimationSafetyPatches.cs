using GraveyardKeeperCoop.Multiplayer;
using GraveyardKeeperCoop.Network;
using HarmonyLib;
using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch]
    internal static class FishingAnimationSafetyPatches
    {
        // Both vanilla animation behaviours write to the single local FishingGUI.
        // Remote fishing animations must never finish our cast or award our catch.
        [HarmonyPatch(typeof(FishingThrowingAnim), nameof(FishingThrowingAnim.OnStateExit))]
        [HarmonyPrefix]
        internal static bool FishingThrowingAnim_OnStateExit_Prefix(Animator animator)
        {
            return CanUpdateLocalFishing(animator);
        }

        [HarmonyPatch(typeof(EndOfAnimEvent), nameof(EndOfAnimEvent.OnStateExit))]
        [HarmonyPrefix]
        internal static bool EndOfAnimEvent_OnStateExit_Prefix(Animator animator)
        {
            return CanUpdateLocalFishing(animator);
        }

        private static bool CanUpdateLocalFishing(Animator animator)
        {
            // Animator exits may run during deferred destruction after the online
            // session flag has already been cleared. A ghost still cannot own GUI state.
            if (animator != null && animator.GetComponentInParent<GhostDriver>() != null)
                return false;

            if (OnlineCoopManager.Instance?.IsOnlineCoopEnabled != true)
                return true;

            WorldGameObject localPlayer = MainGame.me?.player;
            return animator != null && localPlayer != null &&
                   animator.transform.IsChildOf(localPlayer.transform);
        }
    }
}

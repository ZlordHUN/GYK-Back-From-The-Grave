using HarmonyLib;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch]
    public static class OnlineCinematicStatePatches
    {
        // Ownership: this class only repairs local cinematic aftermath. It does
        // not decide whether a cutscene should sync and does not suppress player
        // enable changes. Run late so feature-specific observers finish first.
        [HarmonyPatch(typeof(GS), nameof(GS.SetPlayerEnable))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        public static void GS_SetPlayerEnable_Postfix(bool player_enabled, bool affect_cinematic)
        {
            if (!player_enabled || !affect_cinematic)
                return;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return;

            RestoreLocalPlayerSpeedFloor();
        }

        private static void RestoreLocalPlayerSpeedFloor()
        {
            var player = MainGame.me?.player;
            if (player?.data == null)
                return;

            float currentSpeed = player.data.GetParam("speed", LazyConsts.PLAYER_SPEED);
            if (currentSpeed > 1.25f)
                return;

            player.data.SetParam("speed", LazyConsts.PLAYER_SPEED);
            CoopMod.Logger.LogInfo($"[OnlineCinematicState] Restored local speed after cinematic: {currentSpeed} -> {LazyConsts.PLAYER_SPEED}");
        }
    }
}

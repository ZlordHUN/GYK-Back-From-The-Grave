using HarmonyLib;
using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Experimental patches for local microstutter/frame-pacing issues.
    /// </summary>
    public static class PerformanceSmoothingPatches
    {
        private static RigidbodyInterpolation2D? lastAppliedInterpolation;
        private static int lastAppliedTargetFrameRate = int.MinValue;
        private static int lastAppliedVSyncCount = int.MinValue;

        [HarmonyPatch(typeof(ComponentsManager), "CheckCharacterStuff")]
        public static class ComponentsManager_CheckCharacterStuff_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(ComponentsManager __instance)
            {
                ApplyLocalPlayerInterpolation(__instance?.wgo);
            }
        }

        [HarmonyPatch(typeof(PlayerComponent), "SpawnPlayer")]
        public static class PlayerComponent_SpawnPlayer_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(bool is_local_player, PlayerComponent __result)
            {
                if (is_local_player)
                {
                    ApplyLocalPlayerInterpolation(__result?.wgo);
                }
            }
        }

        [HarmonyPatch(typeof(MainGame), "OnGameStartedPlaying")]
        public static class MainGame_OnGameStartedPlaying_Patch
        {
            [HarmonyPostfix]
            public static void Postfix()
            {
                ApplyLocalPlayerInterpolation(MainGame.me?.player);
                ApplyFramePacingOverride(forceLog: true);
            }
        }

        [HarmonyPatch(typeof(MainGame), "Update")]
        public static class MainGame_Update_Patch
        {
            private static long __start;

            [HarmonyPrefix]
            public static void Prefix()
            {
                __start = GraveyardKeeperCoop.Utils.FrameProfiler.BeginSection();
            }

            [HarmonyPostfix]
            public static void Postfix()
            {
                if (__start != 0)
                {
                    GraveyardKeeperCoop.Utils.FrameProfiler.EndSection("MainGame.Update", __start);
                    __start = 0;
                }
                ApplyFramePacingOverride(forceLog: false);
            }
        }

        private static void ApplyLocalPlayerInterpolation(WorldGameObject player)
        {
            if (ModConfig.EnableLocalMotionSmoothing?.Value != true || player == null)
                return;

            if (MainGame.me?.player == null || player != MainGame.me.player)
                return;

            if (!player.is_player)
                return;

            Rigidbody2D body = player.components?.character?.body ?? player.GetComponent<Rigidbody2D>();
            if (body == null || body.interpolation == RigidbodyInterpolation2D.Interpolate)
                return;

            body.interpolation = RigidbodyInterpolation2D.Interpolate;
            if (lastAppliedInterpolation != RigidbodyInterpolation2D.Interpolate)
            {
                lastAppliedInterpolation = RigidbodyInterpolation2D.Interpolate;
                CoopMod.Logger.LogInfo("[PerformanceSmoothing] Local player Rigidbody2D interpolation set to Interpolate");
            }
        }

        private static void ApplyFramePacingOverride(bool forceLog)
        {
            if (ModConfig.OverrideFramePacing?.Value != true)
                return;

            int targetFrameRate = Mathf.Clamp(ModConfig.TargetFrameRate?.Value ?? 60, 30, 240);
            bool needsApply = QualitySettings.vSyncCount != 0 || Application.targetFrameRate != targetFrameRate;
            if (needsApply)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = targetFrameRate;
            }

            if (forceLog || needsApply || lastAppliedVSyncCount != 0 || lastAppliedTargetFrameRate != targetFrameRate)
            {
                lastAppliedVSyncCount = 0;
                lastAppliedTargetFrameRate = targetFrameRate;
                CoopMod.Logger.LogInfo($"[PerformanceSmoothing] Frame pacing override active: vSyncCount=0, targetFrameRate={targetFrameRate}");
            }
        }
    }
}

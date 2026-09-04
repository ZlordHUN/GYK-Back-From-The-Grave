using HarmonyLib;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch]
    public static class OnlineLightingPatches
    {
        private static EnvironmentPreset patchedPreset;
        private static bool restorePresetFlags;
        private static bool originalLightOverride;
        private static bool originalAmbientLightOverride;
        private static bool originalLightSpritesOverride;
        private static bool originalForceStaticTime;
        private static bool originalForceShadowsAlpha;
        private static bool originalForceLightIntensity;
        private static bool originalForceGlobalShadowsAlpha;
        private static bool loggedActive;

        // Forces indoor scenes onto the dynamic time-of-day lighting path while online.
        // This can be expensive because it makes the native renderer recalculate
        // lighting and shadows for the indoor scene every frame.
        private static readonly bool IndoorLightingSyncEnabled = true;

        [HarmonyPatch(typeof(TimeOfDay), "Update")]
        [HarmonyPrefix]
        public static void TimeOfDay_Update_Prefix()
        {
            restorePresetFlags = false;

            if (!ShouldIndoorLightingFollowSyncedTime())
                return;

            patchedPreset = EnvironmentEngine.cur_preset;
            originalLightOverride = patchedPreset.light_override;
            originalAmbientLightOverride = patchedPreset.ambient_light_override;
            originalLightSpritesOverride = patchedPreset.light_sprites_override;
            originalForceStaticTime = patchedPreset.force_static_time;
            originalForceShadowsAlpha = patchedPreset.force_shadows_alpha;
            originalForceLightIntensity = patchedPreset.force_light_intensity;
            originalForceGlobalShadowsAlpha = patchedPreset.force_global_shadows_alpha;
            restorePresetFlags = true;

            patchedPreset.light_override = false;
            patchedPreset.ambient_light_override = false;
            patchedPreset.light_sprites_override = false;
            patchedPreset.force_static_time = false;
            patchedPreset.force_shadows_alpha = false;
            patchedPreset.force_light_intensity = false;
            patchedPreset.force_global_shadows_alpha = false;

            if (EnvironmentEngine.me?.lut_effect_timeofday != null)
            {
                EnvironmentEngine.me.lut_effect_timeofday.enabled = true;
            }

            if (!loggedActive)
            {
                loggedActive = true;
                CoopMod.Logger.LogInfo($"[OnlineLighting] Indoor lighting follows synced time while online (preset='{patchedPreset.name}')");
            }
        }

        [HarmonyPatch(typeof(TimeOfDay), "Update")]
        [HarmonyPostfix]
        public static void TimeOfDay_Update_Postfix()
        {
            if (!restorePresetFlags || patchedPreset == null)
                return;

            patchedPreset.light_override = originalLightOverride;
            patchedPreset.ambient_light_override = originalAmbientLightOverride;
            patchedPreset.light_sprites_override = originalLightSpritesOverride;
            patchedPreset.force_static_time = originalForceStaticTime;
            patchedPreset.force_shadows_alpha = originalForceShadowsAlpha;
            patchedPreset.force_light_intensity = originalForceLightIntensity;
            patchedPreset.force_global_shadows_alpha = originalForceGlobalShadowsAlpha;

            restorePresetFlags = false;
            patchedPreset = null;
        }

        private static bool ShouldIndoorLightingFollowSyncedTime()
        {
            if (!IndoorLightingSyncEnabled)
                return false;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return false;

            if (!MainGame.game_started || EnvironmentEngine.me?.data == null)
                return false;

            if (EnvironmentEngine.me.data.state != EnvironmentEngine.State.Inside)
                return false;

            EnvironmentPreset preset = EnvironmentEngine.cur_preset;
            return preset != null
                && preset.name != null
                && preset.name.StartsWith("inside", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}

using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Keeps the vanilla GameGUI inside short viewports. The tab bar and each
    /// vanilla tab are separate GUI roots, so they must share the same scale.
    /// </summary>
    [HarmonyPatch]
    public static class GameGUIResponsiveScalePatch
    {
        private const float FitReferenceHeight = 800f;
        // Reserve enough room for the HUD toolbar below the full-height GameGUI.
        private const float FitVerticalMargin = 144f;
        private const float MinimumScale = 0.72f;

        private static readonly Dictionary<Transform, Vector3> NativeScales =
            new Dictionary<Transform, Vector3>();

        private static int _appliedScreenWidth = -1;
        private static int _appliedScreenHeight = -1;

        [HarmonyPatch(typeof(GameGUI), nameof(GameGUI.Open))]
        [HarmonyPostfix]
        private static void GameGUIOpenPostfix(GameGUI __instance)
        {
            Apply(__instance, true);
        }

        [HarmonyPatch(typeof(GameGUI), nameof(GameGUI.SelectTab))]
        [HarmonyPostfix]
        private static void GameGUISelectTabPostfix(GameGUI __instance)
        {
            Apply(__instance, false);
        }

        [HarmonyPatch(typeof(GameGUI), nameof(GameGUI.Update))]
        [HarmonyPostfix]
        private static void GameGUIUpdatePostfix(GameGUI __instance)
        {
            if (__instance != null && __instance.is_shown)
                Apply(__instance, false);
        }

        [HarmonyPatch(typeof(GameGUI), nameof(GameGUI.Hide))]
        [HarmonyPostfix]
        private static void GameGUIHidePostfix()
        {
            RestoreNativeScales();
        }

        private static void Apply(GameGUI gameGui, bool forceLog)
        {
            if (gameGui == null)
                return;

            int screenWidth = Screen.width;
            int screenHeight = Screen.height;
            float scale = Mathf.Clamp(
                (screenHeight - FitVerticalMargin) / FitReferenceHeight,
                MinimumScale,
                1f);

            bool resolutionChanged = screenWidth != _appliedScreenWidth ||
                                     screenHeight != _appliedScreenHeight;
            _appliedScreenWidth = screenWidth;
            _appliedScreenHeight = screenHeight;

            ApplyScale(gameGui.transform, scale);

            GUIElements gui = GUIElements.me;
            if (gui != null)
            {
                ApplyScale(gui.inventory?.transform, scale);
                ApplyScale(gui.tech_tree?.transform, scale);
                ApplyScale(gui.npcs_list?.transform, scale);
                ApplyScale(gui.map?.transform, scale);
            }

            // ChatGUI already owns resolution-specific content scaling. Scaling
            // that root here as well would compound its low-resolution layout.
            if (forceLog || resolutionChanged)
            {
                CoopMod.Logger.LogInfo(
                    $"[GameGUI] Applied responsive layout for {screenWidth}x{screenHeight}: " +
                    $"rootScale={scale:F3}");
            }
        }

        private static void ApplyScale(Transform target, float scale)
        {
            if (target == null)
                return;

            if (!NativeScales.TryGetValue(target, out Vector3 nativeScale))
            {
                nativeScale = target.localScale;
                NativeScales[target] = nativeScale;
            }

            target.localScale = new Vector3(
                nativeScale.x * scale,
                nativeScale.y * scale,
                nativeScale.z);
        }

        private static void RestoreNativeScales()
        {
            if (NativeScales.Count == 0)
                return;

            foreach (KeyValuePair<Transform, Vector3> entry in NativeScales)
            {
                if (entry.Key != null)
                    entry.Key.localScale = entry.Value;
            }

            NativeScales.Clear();
            _appliedScreenWidth = -1;
            _appliedScreenHeight = -1;
        }
    }
}

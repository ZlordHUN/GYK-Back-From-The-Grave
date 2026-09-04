using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    internal static class ControllerIconDiagnostics
    {
        private const int MaxUniqueLogsPerKind = 80;
        private static readonly Dictionary<string, float> lastLogTimes = new Dictionary<string, float>();
        private static readonly HashSet<string> uniqueLogKeys = new HashSet<string>();

        internal static bool LooksLikeControllerAIcon(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            return text.Contains("(A)")
                   || text.Contains("(XBOXA)")
                   || text.Contains("(XB1A)")
                   || text.Contains("(X360A)")
                   || text.Contains("(PSA)")
                   || text.Contains("(NSWITCHA)");
        }

        internal static bool ShouldLog(string kind, string detail, float minIntervalSeconds = 0.25f)
        {
            string uniqueKey = $"{kind}:{detail}";
            if (!uniqueLogKeys.Contains(uniqueKey))
            {
                if (uniqueLogKeys.Count >= MaxUniqueLogsPerKind)
                    return false;

                uniqueLogKeys.Add(uniqueKey);
                lastLogTimes[uniqueKey] = Time.realtimeSinceStartup;
                return true;
            }

            float now = Time.realtimeSinceStartup;
            if (lastLogTimes.TryGetValue(uniqueKey, out float lastTime) && now - lastTime < minIntervalSeconds)
                return false;

            lastLogTimes[uniqueKey] = now;
            return true;
        }

        internal static string BuildContext(UnityEngine.Object contextObject = null)
        {
            string objectPath = contextObject != null ? GetObjectPath(contextObject) : "null";
            return $"time={Time.realtimeSinceStartup:F2}, lazyGamepadActive={SafeLazyGamepadActive()}, object={objectPath}";
        }

        internal static string GetObjectPath(UnityEngine.Object contextObject)
        {
            if (contextObject == null)
                return "null";

            GameObject gameObject = null;
            if (contextObject is Component component)
                gameObject = component.gameObject;
            else if (contextObject is GameObject go)
                gameObject = go;

            if (gameObject == null)
                return contextObject.name ?? contextObject.GetType().Name;

            Transform current = gameObject.transform;
            string path = current.name;
            while (current.parent != null)
            {
                current = current.parent;
                path = current.name + "/" + path;
            }

            return path;
        }

        internal static string CompactStack()
        {
            try
            {
                var trace = new System.Diagnostics.StackTrace(2, false);
                var frames = trace.GetFrames();
                if (frames == null)
                    return "stack unavailable";

                var parts = new List<string>();
                for (int i = 0; i < frames.Length && parts.Count < 10; i++)
                {
                    MethodBase method = frames[i].GetMethod();
                    if (method == null)
                        continue;

                    string typeName = method.DeclaringType?.Name ?? "<null>";
                    if (typeName.StartsWith("ControllerIcon", StringComparison.Ordinal)
                        || typeName.StartsWith("ManualLogSource", StringComparison.Ordinal)
                        || typeName.StartsWith("Logger", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    parts.Add($"{typeName}.{method.Name}");
                }

                return string.Join(" <- ", parts.ToArray());
            }
            catch (Exception ex)
            {
                return "stack error: " + ex.GetType().Name;
            }
        }

        private static bool SafeLazyGamepadActive()
        {
            try
            {
                return LazyInput.gamepad_active;
            }
            catch
            {
                return false;
            }
        }
    }

    // DISABLED: interferes with NGUI input (ChatOverlay)
    //[HarmonyPatch(typeof(GameKeyTip), nameof(GameKeyTip.GetIcon))]
    public static class GameKeyTipGetIconDiagnosticPatch
    {
        static void Postfix(GameKey key, ref string __result)
        {
            try
            {
                if (!ControllerIconDiagnostics.LooksLikeControllerAIcon(__result))
                    return;

                string detail = $"{key}:{__result}:{ControllerIconDiagnostics.CompactStack()}";
                if (!ControllerIconDiagnostics.ShouldLog("GameKeyTip.GetIcon", detail, 1f))
                    return;

                CoopMod.Logger.LogInfo($"[ControllerIconDiag] GameKeyTip.GetIcon key={key}, result='{__result}', {ControllerIconDiagnostics.BuildContext()}, stack={ControllerIconDiagnostics.CompactStack()}");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[ControllerIconDiag] GameKeyTip.GetIcon diagnostic failed: {ex.Message}");
            }
        }
    }

    // DISABLED: interferes with NGUI input (ChatOverlay)
    //[HarmonyPatch(typeof(ButtonTipsStr), nameof(ButtonTipsStr.Print), new[] { typeof(List<GameKeyTip>), typeof(string) })]
    public static class ButtonTipsStrPrintListDiagnosticPatch
    {
        static void Postfix(ButtonTipsStr __instance)
        {
            try
            {
                string text = __instance?.label?.text;
                if (!ControllerIconDiagnostics.LooksLikeControllerAIcon(text))
                    return;

                string objectPath = ControllerIconDiagnostics.GetObjectPath(__instance);
                string detail = $"{objectPath}:{text}:{ControllerIconDiagnostics.CompactStack()}";
                if (!ControllerIconDiagnostics.ShouldLog("ButtonTipsStr.PrintList", detail, 1f))
                    return;

                CoopMod.Logger.LogInfo($"[ControllerIconDiag] ButtonTipsStr.Print label='{text}', {ControllerIconDiagnostics.BuildContext(__instance)}, stack={ControllerIconDiagnostics.CompactStack()}");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[ControllerIconDiag] ButtonTipsStr.Print diagnostic failed: {ex.Message}");
            }
        }
    }

    // DISABLED: interferes with NGUI input (ChatOverlay)
    //[HarmonyPatch(typeof(ButtonTipsStr), nameof(ButtonTipsStr.Print), new[] { typeof(GameKeyTip) })]
    public static class ButtonTipsStrPrintSingleDiagnosticPatch
    {
        static void Postfix(ButtonTipsStr __instance)
        {
            try
            {
                string text = __instance?.label?.text;
                if (!ControllerIconDiagnostics.LooksLikeControllerAIcon(text))
                    return;

                string objectPath = ControllerIconDiagnostics.GetObjectPath(__instance);
                string detail = $"{objectPath}:{text}:{ControllerIconDiagnostics.CompactStack()}";
                if (!ControllerIconDiagnostics.ShouldLog("ButtonTipsStr.PrintSingle", detail, 1f))
                    return;

                CoopMod.Logger.LogInfo($"[ControllerIconDiag] ButtonTipsStr.Print(single) label='{text}', {ControllerIconDiagnostics.BuildContext(__instance)}, stack={ControllerIconDiagnostics.CompactStack()}");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[ControllerIconDiag] ButtonTipsStr.Print(single) diagnostic failed: {ex.Message}");
            }
        }
    }

    // DISABLED: interferes with NGUI input (ChatOverlay)
    //[HarmonyPatch(typeof(UILabel), "set_text")]
    public static class UILabelSetTextControllerIconDiagnosticPatch
    {
        static void Prefix(UILabel __instance, string value)
        {
            try
            {
                if (!ControllerIconDiagnostics.LooksLikeControllerAIcon(value))
                    return;

                string objectPath = ControllerIconDiagnostics.GetObjectPath(__instance);
                string detail = $"{objectPath}:{value}:{ControllerIconDiagnostics.CompactStack()}";
                if (!ControllerIconDiagnostics.ShouldLog("UILabel.set_text", detail, 1f))
                    return;

                CoopMod.Logger.LogInfo($"[ControllerIconDiag] UILabel.text='{value}', {ControllerIconDiagnostics.BuildContext(__instance)}, stack={ControllerIconDiagnostics.CompactStack()}");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[ControllerIconDiag] UILabel.text diagnostic failed: {ex.Message}");
            }
        }
    }
}

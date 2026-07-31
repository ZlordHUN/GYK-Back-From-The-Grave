using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using GraveyardKeeperCoop.Multiplayer;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch]
    internal static class AmbientSpeechSyncPatches
    {
        private static MethodBase TargetMethod()
        {
            MethodInfo[] methods = typeof(WorldGameObject).GetMethods(BindingFlags.Instance | BindingFlags.Public);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name == "Say" && method.GetParameters().Length >= 7)
                    return method;
            }

            return null;
        }

        private static bool Prefix(WorldGameObject __instance, object[] __args)
        {
            if (__instance == null || __args == null || __args.Length < 2)
                return true;

            string text = __args[0] as string;
            if (!string.Equals(
                    text,
                    "disabled_interactions",
                    StringComparison.Ordinal) ||
                !CutsceneSyncPatches.ShouldSuppressTransientDisabledInteraction())
            {
                return true;
            }

            // Vanilla uses this callback to clear its "bubble already shown" guard.
            // Run it next frame: CheckIfDisabledInTutorial sets its guard after Say
            // returns, so a synchronous callback would be overwritten immediately.
            if (__args[1] is GJCommons.VoidDelegate onDisappeared)
                CoopMod.Instance?.StartCoroutine(
                    CompleteSuppressedBubbleNextFrame(onDisappeared));

            CoopMod.Logger.LogInfo(
                "[CutsceneSync] Suppressed transient \"Not right now\" bubble during cutscene handoff");
            return false;
        }

        private static IEnumerator CompleteSuppressedBubbleNextFrame(
            GJCommons.VoidDelegate onDisappeared)
        {
            yield return null;
            onDisappeared?.Invoke();
        }

        private static void Postfix(WorldGameObject __instance, object[] __args)
        {
            if (DialogueSync.IsApplyingAmbientSpeech || __instance == null || __args == null || __args.Length < 7)
                return;

            string text = __args[0] as string;
            if (string.IsNullOrEmpty(text))
                return;
            if (string.Equals(
                    text,
                    "disabled_interactions",
                    StringComparison.Ordinal) &&
                CutsceneSyncPatches.ShouldSuppressTransientDisabledInteraction())
            {
                return;
            }

            int bubbleType = 0;
            if (__args[3] is Enum bubbleEnum)
                bubbleType = Convert.ToInt32(bubbleEnum);

            bool sayAsPlayer = __args[5] is bool value && value;
            DialogueSync.Instance?.NotifyAmbientSpeechBubble(__instance, text, bubbleType, sayAsPlayer);
        }
    }
}

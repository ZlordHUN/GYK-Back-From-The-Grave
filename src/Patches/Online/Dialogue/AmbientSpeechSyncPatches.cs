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

        private static bool Prefix(
            WorldGameObject __instance,
            object[] __args,
            ref bool __state)
        {
            __state = false;
            if (__instance == null || __args == null || __args.Length < 2)
                return true;

            string text = __args[0] as string;
            if (GameTimeSync.Instance
                    ?.TryConsumeSynchronizedWakeSpeechSuppression(
                        __instance,
                        text) == true)
            {
                __state = true;
                CompleteSuppressedBubble(
                    __args[1] as GJCommons.VoidDelegate);
                CoopMod.Logger.LogInfo(
                    "[SleepSync] Suppressed duplicate client wake-up speech; " +
                    "the host is the synchronized speaker");
                return false;
            }

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
            __state = true;
            CompleteSuppressedBubble(
                __args[1] as GJCommons.VoidDelegate);

            CoopMod.Logger.LogInfo(
                "[CutsceneSync] Suppressed transient \"Not right now\" bubble during cutscene handoff");
            return false;
        }

        private static void CompleteSuppressedBubble(
            GJCommons.VoidDelegate onDisappeared)
        {
            if (onDisappeared == null)
                return;

            if (CoopMod.Instance != null)
            {
                try
                {
                    CoopMod.Instance.StartCoroutine(
                        CompleteSuppressedBubbleNextFrame(onDisappeared));
                    return;
                }
                catch (Exception ex)
                {
                    CoopMod.Logger.LogWarning(
                        "[DialogueSync] Could not schedule suppressed speech " +
                        $"completion: {ex.Message}");
                }
            }

            // Session teardown can destroy the runner before a late Say call.
            // Completing inline is safer than leaving the owning FlowScript stuck.
            InvokeSuppressedBubbleCompletion(onDisappeared);
        }

        private static IEnumerator CompleteSuppressedBubbleNextFrame(
            GJCommons.VoidDelegate onDisappeared)
        {
            yield return null;
            InvokeSuppressedBubbleCompletion(onDisappeared);
        }

        private static void InvokeSuppressedBubbleCompletion(
            GJCommons.VoidDelegate onDisappeared)
        {
            try
            {
                onDisappeared?.Invoke();
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[DialogueSync] Suppressed speech callback failed: {ex.Message}");
            }
        }

        private static void Postfix(
            WorldGameObject __instance,
            object[] __args,
            bool __state)
        {
            // Harmony still runs postfixes when a prefix skips the original. Do not
            // turn a locally suppressed bubble back into a network speech event.
            if (__state)
                return;

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

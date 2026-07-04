using System;
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

        private static void Postfix(WorldGameObject __instance, object[] __args)
        {
            if (DialogueSync.IsApplyingAmbientSpeech || __instance == null || __args == null || __args.Length < 7)
                return;

            string text = __args[0] as string;
            if (string.IsNullOrEmpty(text))
                return;

            int bubbleType = 0;
            if (__args[3] is Enum bubbleEnum)
                bubbleType = Convert.ToInt32(bubbleEnum);

            bool sayAsPlayer = __args[5] is bool value && value;
            DialogueSync.Instance?.NotifyAmbientSpeechBubble(__instance, text, bubbleType, sayAsPlayer);
        }
    }
}

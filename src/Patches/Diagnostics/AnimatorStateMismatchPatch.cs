using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using GraveyardKeeperCoop.Multiplayer;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Prevents base-game NPC animator state desync from flooding Unity's error log.
    /// This can happen during delayed multiplayer intro/startup flows when an NPC starts
    /// walking before its Animator parameter has caught up to BaseCharacterComponent.
    /// </summary>
    [HarmonyPatch]
    public static class AnimatorStateMismatchPatch
    {
        private const string WalkingIdleMismatchPrefix = "Animator error: _global_state: -1 differs from the animator: 0 for wgo:";
        private const string IdleWalkingMismatchPrefix = "Animator error: _global_state: 0 differs from the animator: -1 for wgo:";
        private const float ReportIntervalSeconds = 5f;
        private const float OnlineStartupReconcileSeconds = 30f;
        private const float CinematicReconcileGraceSeconds = 5f;
        private static readonly FieldInfo GlobalStateField = AccessTools.Field(typeof(BaseCharacterComponent), "_global_state");
        private static readonly Dictionary<int, float> LastFilteredReportByContext = new Dictionary<int, float>();
        private static float reconcileAllowedUntil;

        [HarmonyPatch(typeof(BaseCharacterComponent), "UpdateComponent")]
        [HarmonyPrefix]
        public static void ReconcileWalkingAnimatorState(BaseCharacterComponent __instance)
        {
            if (!ShouldReconcileNpcAnimatorState())
                return;

            if (__instance?.wgo == null || __instance.wgo.is_player || GlobalStateField == null)
                return;

            var animator = __instance.wgo.components?.animator;
            if (animator == null || !animator.ParamExists("global_state"))
                return;

            int characterGlobalState;
            try
            {
                characterGlobalState = (int)GlobalStateField.GetValue(__instance);
            }
            catch
            {
                return;
            }

            if (characterGlobalState != (int)CharAnimState.Walking && characterGlobalState != (int)CharAnimState.Idle)
                return;

            int animatorGlobalState;
            try
            {
                animatorGlobalState = animator.GetInteger("global_state");
            }
            catch
            {
                return;
            }

            if (animatorGlobalState == (int)CharAnimState.Idle)
            {
                animator.SetInteger("global_state", characterGlobalState);
            }
            else if (characterGlobalState == (int)CharAnimState.Idle && animatorGlobalState == (int)CharAnimState.Walking)
            {
                animator.SetInteger("global_state", characterGlobalState);
            }
        }

        private static bool ShouldReconcileNpcAnimatorState()
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
            {
                reconcileAllowedUntil = 0f;
                return false;
            }

            float now = Time.realtimeSinceStartup;
            if (reconcileAllowedUntil <= 0f)
            {
                reconcileAllowedUntil = now + OnlineStartupReconcileSeconds;
            }

            if (GameLoadSync.Instance?.IsInIntroPhase == true)
            {
                reconcileAllowedUntil = Mathf.Max(reconcileAllowedUntil, now + CinematicReconcileGraceSeconds);
                return true;
            }

            bool localControlDisabled = MainGame.me?.player_char != null && !MainGame.me.player_char.control_enabled;
            if (localControlDisabled)
            {
                reconcileAllowedUntil = Mathf.Max(reconcileAllowedUntil, now + CinematicReconcileGraceSeconds);
                return true;
            }

            return now <= reconcileAllowedUntil;
        }

        [HarmonyPatch(typeof(Debug), nameof(Debug.LogError), new[] { typeof(object), typeof(UnityEngine.Object) })]
        [HarmonyPrefix]
        public static bool FilterKnownWalkingIdleMismatch(object message, UnityEngine.Object context)
        {
            string text = message as string;
            if (string.IsNullOrEmpty(text) ||
                (!text.StartsWith(WalkingIdleMismatchPrefix) && !text.StartsWith(IdleWalkingMismatchPrefix)))
            {
                return true;
            }

            int contextId = context != null ? context.GetInstanceID() : 0;
            float now = Time.realtimeSinceStartup;

            float lastReport;
            if (!LastFilteredReportByContext.TryGetValue(contextId, out lastReport) || now - lastReport >= ReportIntervalSeconds)
            {
                LastFilteredReportByContext[contextId] = now;
                CoopMod.Logger.LogWarning($"[AnimatorStateMismatchPatch] Suppressed repeated NPC walking animator mismatch: {text}");
            }

            return false;
        }
    }
}

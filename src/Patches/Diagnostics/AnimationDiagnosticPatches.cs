using HarmonyLib;
using BepInEx.Logging;
using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Diagnostic logging for animation state changes.
    /// Traces the full flow: SetAnimationState → SetGlobalState → Animator
    /// to understand why digging animations don't appear on the remote player's screen.
    /// </summary>
    [HarmonyPatch]
    public static class AnimationDiagnosticPatches
    {
        private static ManualLogSource Logger => CoopMod.Logger;

        // Throttle logging to avoid spam during continuous tool use
        private static float lastLogTime;
        private const float LOG_INTERVAL = 0.25f;

        /// <summary>
        /// Logs every SetAnimationState call — this is the high-level entry point.
        /// Called by ToolComponent, PlayerComponent, BaseCharacterAttack, etc.
        /// </summary>
        [HarmonyPatch(typeof(BaseCharacterComponent), "SetAnimationState")]
        [HarmonyPrefix]
        public static void SetAnimationState_Prefix(BaseCharacterComponent __instance, CharAnimState state, ItemDefinition.ItemType item_type)
        {
            // Only log non-idle/non-walking states (those are spammy) OR log all for player characters
            bool isPlayer = __instance.wgo != null && __instance.wgo.is_player;
            bool isInteresting = state != CharAnimState.Idle && state != CharAnimState.Walking;

            if (isPlayer || isInteresting)
            {
                if (Time.time - lastLogTime < LOG_INTERVAL && !isInteresting)
                    return;
                lastLogTime = Time.time;

                string wgoName = __instance.wgo?.obj_id ?? "unknown";
                bool isLocalPlayer = __instance.wgo == MainGame.me?.player;
                string playerTag = isLocalPlayer ? "LOCAL" : (isPlayer ? "REMOTE?" : "NPC");

                Logger.LogDebug($"[ANIM DIAG] SetAnimationState: [{playerTag}] {wgoName} → state={state}, item_type={item_type}");

                // Log stack trace for tool animations to see what triggered them
                if (state == CharAnimState.Tool)
                {
                    Logger.LogDebug($"[ANIM DIAG]   Tool animation! global_state will be {100 + (int)item_type} (tool: {item_type})");
                    Logger.LogDebug($"[ANIM DIAG]   Stack: {System.Environment.StackTrace}");
                }
            }
        }

        /// <summary>
        /// Logs the low-level SetGlobalState(int) that directly drives the Unity Animator.
        /// This is where the rubber meets the road — the animator parameter gets set here.
        /// </summary>
        [HarmonyPatch(typeof(BaseCharacterComponent), "SetGlobalState", typeof(int))]
        [HarmonyPrefix]
        public static void SetGlobalState_Int_Prefix(BaseCharacterComponent __instance, int new_state)
        {
            bool isPlayer = __instance.wgo != null && __instance.wgo.is_player;
            
            // Log all non-trivial state changes for player characters
            if (isPlayer && new_state != 0 && new_state != -1)
            {
                string wgoName = __instance.wgo?.obj_id ?? "unknown";
                bool isLocalPlayer = __instance.wgo == MainGame.me?.player;
                string playerTag = isLocalPlayer ? "LOCAL" : "REMOTE?";

                Logger.LogDebug($"[ANIM DIAG] SetGlobalState(int): [{playerTag}] {wgoName} → global_state={new_state}");
            }
        }

        /// <summary>
        /// Logs ToolComponent.UseCurrentTool — this is the method that initiates tool use 
        /// (including digging with shovel on graves).
        /// </summary>
        [HarmonyPatch(typeof(ToolComponent), "UseCurrentTool")]
        [HarmonyPrefix]
        public static void UseCurrentTool_Prefix(ToolComponent __instance, bool placed_on_dock_point)
        {
            var wgo = __instance.wgo;
            if (wgo == null || !wgo.is_player) return;

            bool isLocalPlayer = wgo == MainGame.me?.player;
            string playerTag = isLocalPlayer ? "LOCAL" : "REMOTE?";

            // Get the tool type
            var tool = wgo.GetEquippedTool();
            string toolName = tool?.definition?.type.ToString() ?? "None";

            // Get the target object via reflection (FindObjectForInteraction is protected)
            string targetName = "unknown";
            try
            {
                var targetField = AccessTools.Field(typeof(ToolComponent), "_target_obj");
                if (targetField != null)
                {
                    var target = targetField.GetValue(__instance) as WorldGameObject;
                    targetName = target?.obj_id ?? "none";
                }
            }
            catch { }

            Logger.LogDebug($"[ANIM DIAG] UseCurrentTool: [{playerTag}] tool={toolName}, target={targetName}, placed_on_dock={placed_on_dock_point}");
        }

        /// <summary>
        /// Logs UseTool — the entry point when a player presses the work button.
        /// Returns true if tool use started.
        /// </summary>
        [HarmonyPatch(typeof(ToolComponent), "UseTool")]
        [HarmonyPrefix]
        public static void UseTool_Prefix(ToolComponent __instance, bool placed_on_dock_point)
        {
            var wgo = __instance.wgo;
            if (wgo == null || !wgo.is_player) return;

            bool isLocalPlayer = wgo == MainGame.me?.player;
            string playerTag = isLocalPlayer ? "LOCAL" : "REMOTE?";

            Logger.LogDebug($"[ANIM DIAG] UseTool: [{playerTag}] placed_on_dock={placed_on_dock_point}");
        }

        /// <summary>
        /// Logs AnimationEventAction — called by Unity animation keyframes to advance craft progress.
        /// This is what makes the shovel "do work" at each dig stroke.
        /// </summary>
        [HarmonyPatch(typeof(ToolComponent), "AnimationEventAction")]
        [HarmonyPrefix]
        public static void AnimationEventAction_Prefix(ToolComponent __instance)
        {
            var wgo = __instance.wgo;
            if (wgo == null || !wgo.is_player) return;

            bool isLocalPlayer = wgo == MainGame.me?.player;
            string playerTag = isLocalPlayer ? "LOCAL" : "REMOTE?";

            Logger.LogDebug($"[ANIM DIAG] AnimationEventAction: [{playerTag}] — animation keyframe fired (work progress tick)");
        }

        /// <summary>
        /// Logs SetToolGraphics — sets the visual tool sprite on the character.
        /// The method takes an int (tool number), not ItemDefinition.ItemType directly.
        /// </summary>
        [HarmonyPatch(typeof(BaseCharacterComponent), "SetToolGraphics", typeof(int))]
        [HarmonyPrefix]
        public static void SetToolGraphics_Prefix(BaseCharacterComponent __instance, int tool_n)
        {
            bool isPlayer = __instance.wgo != null && __instance.wgo.is_player;
            if (!isPlayer) return;

            bool isLocalPlayer = __instance.wgo == MainGame.me?.player;
            string playerTag = isLocalPlayer ? "LOCAL" : "REMOTE?";

            Logger.LogDebug($"[ANIM DIAG] SetToolGraphics: [{playerTag}] tool_n={tool_n}");
        }

        /// <summary>
        /// Logs OnStartWalking and OnStopped — these are what the remote player currently
        /// receives via OnRemotePlayerStateReceived. Helps confirm the idle/walk cycle.
        /// </summary>
        [HarmonyPatch(typeof(BaseCharacterComponent), "OnStartWalking")]
        [HarmonyPrefix]
        public static void OnStartWalking_Prefix(BaseCharacterComponent __instance)
        {
            bool isPlayer = __instance.wgo != null && __instance.wgo.is_player;
            if (!isPlayer) return;

            bool isLocalPlayer = __instance.wgo == MainGame.me?.player;
            if (isLocalPlayer) return; // Only log for remote player — local walking is spammy

            Logger.LogDebug($"[ANIM DIAG] OnStartWalking: [REMOTE] — remote player set to walking animation");
        }

        [HarmonyPatch(typeof(BaseCharacterComponent), "OnStopped")]
        [HarmonyPrefix]
        public static void OnStopped_Prefix(BaseCharacterComponent __instance)
        {
            bool isPlayer = __instance.wgo != null && __instance.wgo.is_player;
            if (!isPlayer) return;

            bool isLocalPlayer = __instance.wgo == MainGame.me?.player;
            if (isLocalPlayer) return; // Only log for remote player

            Logger.LogDebug($"[ANIM DIAG] OnStopped: [REMOTE] — remote player set to idle animation");
        }
    }
}

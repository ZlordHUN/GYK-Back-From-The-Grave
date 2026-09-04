using System;
using System.Reflection;
using GraveyardKeeperCoop.Multiplayer;
using GraveyardKeeperCoop.Network;
using HarmonyLib;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Tracks the local player's actual tool-work lifecycle. The target WGO owns
    /// the progress value, while WorkIndicatorSync owns which player it follows.
    /// </summary>
    [HarmonyPatch]
    internal static class WorkIndicatorSyncPatches
    {
        private sealed class ToolUseLeaseResume
        {
            private readonly ToolComponent tool;
            private readonly WorldGameObject target;
            private readonly CraftComponent craft;
            private readonly bool placedOnDockPoint;

            public ToolUseLeaseResume(
                ToolComponent tool,
                WorldGameObject target,
                CraftComponent craft,
                bool placedOnDockPoint)
            {
                this.tool = tool;
                this.target = target;
                this.craft = craft;
                this.placedOnDockPoint = placedOnDockPoint;
            }

            public void Invoke()
            {
                ResumeGrantedToolUse(
                    tool,
                    target,
                    craft,
                    placedOnDockPoint);
            }
        }

        private static readonly MethodInfo UseCurrentToolMethod =
            AccessTools.DeclaredMethod(
                typeof(ToolComponent),
                "UseCurrentTool",
                new[] { typeof(bool) });

        private static bool IsOnlineSyncActive()
        {
            OnlineCoopManager coop = OnlineCoopManager.Instance;
            return coop != null && coop.IsOnlineCoopEnabled;
        }

        [HarmonyPatch(
            typeof(HPActionComponent),
            nameof(HPActionComponent.DoAction),
            new[] { typeof(WorldGameObject), typeof(float), typeof(bool) })]
        [HarmonyPrefix]
        private static void HPActionComponent_DoAction_Prefix(
            HPActionComponent __instance,
            out bool __state)
        {
            __state = IsOnlineSyncActive() &&
                __instance?.wgo != null &&
                __instance.wgo.hp > 0f;
        }

        [HarmonyPatch(
            typeof(HPActionComponent),
            nameof(HPActionComponent.DoAction),
            new[] { typeof(WorldGameObject), typeof(float), typeof(bool) })]
        [HarmonyPostfix]
        private static void HPActionComponent_DoAction_Postfix(
            HPActionComponent __instance,
            ref bool __result,
            bool __state,
            ref bool ____hp_was_positive)
        {
            if (!__state ||
                __result ||
                __instance?.wgo == null ||
                __instance.wgo.hp > 0f)
            {
                return;
            }

            // Synced/reinitialized WGOs can occasionally reach zero with the
            // vanilla transition latch cleared. Restore the latch and result so
            // ToolComponent stops cleanly and the enclosing WGO action can run
            // the normal zero-HP activity.
            ____hp_was_positive = true;
            __result = true;
            CoopMod.Logger.LogWarning(
                $"[WorkIndicatorSync] Restored terminal HP transition for " +
                $"{__instance.wgo.obj_id}#{__instance.wgo.unique_id}");
        }

        [HarmonyPatch(
            typeof(WorldGameObject),
            nameof(WorldGameObject.DoAction),
            new[] { typeof(WorldGameObject), typeof(float) })]
        [HarmonyPrefix]
        private static void WorldGameObject_DoAction_Prefix(
            WorldGameObject __instance,
            out bool __state)
        {
            HPActionComponent hp = __instance?.components?.hp;
            __state = IsOnlineSyncActive() &&
                hp != null &&
                hp.enabled &&
                hp.HasHPInDefinition() &&
                __instance.hp > 0f;
        }

        [HarmonyPatch(
            typeof(WorldGameObject),
            nameof(WorldGameObject.DoAction),
            new[] { typeof(WorldGameObject), typeof(float) })]
        [HarmonyPostfix]
        private static void WorldGameObject_DoAction_Postfix(
            WorldGameObject __instance,
            ref bool __result,
            bool __state)
        {
            if (!__state ||
                __instance == null ||
                __instance.hp > 0f ||
                __instance.is_removed)
            {
                return;
            }

            // Vanilla defers terminal HP work to a later component update.
            // Canonically restored multiplayer WGOs can miss that update, leaving
            // a full progress bar on an unfinished object. The component action
            // loop has finished at this point, so finalizing through the vanilla
            // update method here is safe.
            __result = true;
            __instance.components?.hp?.UpdateComponent(0f);
        }

        [HarmonyPatch(typeof(ToolComponent), "UseCurrentTool")]
        [HarmonyPrefix]
        private static bool UseCurrentTool_Prefix(
            ToolComponent __instance,
            bool placed_on_dock_point,
            out bool __state)
        {
            __state = true;
            WorldGameObject target =
                __instance?.wgo?.components?.interaction?.nearest;
            if (target == null)
                return true;

            CraftComponent craft =
                WorkIndicatorSync.GetExclusiveLocalCraftTarget(__instance, target);
            CraftSync sync = CraftSync.Instance;
            if (craft == null || sync == null)
                return true;

            CraftSync.StationLeaseDisposition disposition =
                sync.PrepareStationLease(craft, craft.current_craft);
            if (disposition == CraftSync.StationLeaseDisposition.Granted)
                return true;

            if (disposition == CraftSync.StationLeaseDisposition.RequestRequired)
            {
                var resume = new ToolUseLeaseResume(
                    __instance,
                    target,
                    craft,
                    placed_on_dock_point);
                sync.RequestStationLease(
                    craft,
                    craft.current_craft,
                    resume.Invoke);
            }

            __state = false;
            return false;
        }

        [HarmonyPatch(typeof(ToolComponent), "UseCurrentTool")]
        [HarmonyPostfix]
        private static void UseCurrentTool_Postfix(
            ToolComponent __instance,
            WorldGameObject ____target_obj,
            bool ____is_using_tool,
            bool __state)
        {
            if (!__state || !____is_using_tool || ____target_obj == null)
                return;

            WorkIndicatorSync.NotifyLocalToolStarted(__instance, ____target_obj);
        }

        private static void ResumeGrantedToolUse(
            ToolComponent tool,
            WorldGameObject expectedTarget,
            CraftComponent craft,
            bool placedOnDockPoint)
        {
            WorldGameObject currentTarget =
                tool?.wgo?.components?.interaction?.nearest;
            bool workStillHeld = tool?.wgo != null &&
                (LazyInput.GetKey(GameKey.Work) || tool.wgo.temp_do_work);
            if (!workStillHeld || currentTarget != expectedTarget)
            {
                CraftSync.Instance?.ReleaseUnusedStationLease(craft);
                return;
            }

            try
            {
                UseCurrentToolMethod?.Invoke(
                    tool,
                    new object[] { placedOnDockPoint });
            }
            catch (Exception ex)
            {
                CraftSync.Instance?.ReleaseUnusedStationLease(craft);
                CoopMod.Logger.LogWarning(
                    $"[WorkIndicatorSync] Could not resume granted workstation use: {ex.Message}");
            }
        }

        [HarmonyPatch(typeof(ToolComponent), "StopUsingTool")]
        [HarmonyPrefix]
        private static void StopUsingTool_Prefix(
            ToolComponent __instance,
            bool work_holded,
            bool stop_working_really)
        {
            // Animation loops briefly release and reacquire the ToolComponent
            // target while the work key remains held. Treat that as one action.
            if (work_holded && !stop_working_really)
                return;

            WorkIndicatorSync.NotifyLocalToolStopped(__instance);
        }
    }
}

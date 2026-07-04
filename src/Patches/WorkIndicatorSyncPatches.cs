using GraveyardKeeperCoop.Multiplayer;
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
        [HarmonyPatch(typeof(ToolComponent), "UseCurrentTool")]
        [HarmonyPostfix]
        private static void UseCurrentTool_Postfix(
            ToolComponent __instance,
            WorldGameObject ____target_obj,
            bool ____is_using_tool)
        {
            if (!____is_using_tool || ____target_obj == null)
                return;

            WorkIndicatorSync.NotifyLocalToolStarted(__instance, ____target_obj);
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

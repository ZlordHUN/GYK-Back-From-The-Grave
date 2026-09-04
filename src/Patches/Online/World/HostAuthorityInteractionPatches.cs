using HarmonyLib;
using GraveyardKeeperCoop.Multiplayer;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch]
    public static class HostAuthorityInteractionPatches
    {
        // Ownership boundary:
        // - CutsceneSyncPatches owns WorldGameObject.AttachFlowScript observation/propagation.
        // - HostAuthorityInteractionSync owns only the initiating Interact/DoZeroHPActivity
        //   authority gates for non-NPC special objects.
        // Keeping AttachFlowScript out of this class avoids competing scripted-flow owners.

        // Run after the observer-style interaction patches. This prefix may suppress
        // the original call, so it should be the last authority decision for Interact.
        [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.Interact))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Low)]
        public static bool Interact_Prefix(WorldGameObject __instance, WorldGameObject other_obj, bool interaction_start, float delta_time)
        {
            if (HostAuthorityInteractionSync.IsApplying)
                return true;

            // Never suppress NPC interactions — NpcInteractionSyncPatches
            // and CutsceneSyncPatches handle those separately.
            if (__instance?.obj_def != null && __instance.obj_def.IsNPC())
                return true;

            if (!HostAuthorityInteractionSync.ShouldRelay(__instance))
                return true;

            if (!HostAuthorityInteractionSync.IsClientRelayActive())
                return true;

            HostAuthorityInteractionSync.RelayLocalInteraction(__instance, interaction_start, delta_time);
            return false;
        }

        // Host-authority relay is the final gate for non-NPC special objects.
        // Let cutscene/NPC/online interaction observers classify the interaction first.
        [HarmonyPatch(typeof(WorldGameObject), "DoZeroHPActivity")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Low)]
        public static bool DoZeroHPActivity_Prefix(WorldGameObject __instance)
        {
            if (HostAuthorityInteractionSync.IsApplying || WGOStateSync.IsProcessingRemote)
                return true;

            if (!HostAuthorityInteractionSync.ShouldRelay(__instance))
                return true;

            if (!HostAuthorityInteractionSync.IsClientRelayActive())
                return true;

            // Keep the world mutation host-authoritative, but do not transfer a
            // player-bound zero-HP FlowScript to the host. Send the mutation first
            // so reliable ordering makes the host apply it before it receives the
            // client's CutsceneSync, then start only the presentation locally.
            HostAuthorityInteractionSync.RelayZeroHpActivity(__instance);
            TryStartLocalZeroHpPresentation(__instance);
            return false;
        }

        private static void TryStartLocalZeroHpPresentation(WorldGameObject wgo)
        {
            string rawScript = wgo?.obj_def?.script_after_hp_0;
            if (string.IsNullOrEmpty(rawScript))
                return;

            try
            {
                CustomFlowScript flowScript;
                if (rawScript.StartsWith("g:"))
                {
                    string scriptName = wgo.ReplaceStringParams(rawScript.Substring(2));
                    flowScript = GS.RunFlowScript(scriptName, null);
                }
                else
                {
                    string scriptName = wgo.ReplaceStringParams(rawScript);
                    flowScript = wgo.AttachFlowScript(scriptName, null, null);
                }

                if (flowScript != null)
                {
                    CoopMod.Logger.LogInfo(
                        $"[HostAuthorityInteractionSync] Started local zero-HP presentation " +
                        $"'{rawScript}' for obj='{wgo.obj_id}' while the host applies the mutation");
                }
                else
                {
                    CoopMod.Logger.LogWarning(
                        $"[HostAuthorityInteractionSync] Local zero-HP presentation " +
                        $"'{rawScript}' returned null for obj='{wgo.obj_id}'");
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[HostAuthorityInteractionSync] Failed to start local zero-HP " +
                    $"presentation for obj='{wgo?.obj_id}': {ex.Message}");
            }
        }
    }
}

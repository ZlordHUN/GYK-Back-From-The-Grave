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

            HostAuthorityInteractionSync.RelayZeroHpActivity(__instance);
            return false;
        }
    }
}

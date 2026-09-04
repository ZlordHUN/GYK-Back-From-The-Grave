using HarmonyLib;
using GraveyardKeeperCoop.Multiplayer;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch]
    internal static class WGORegistryLifecyclePatches
    {
        [HarmonyPatch(typeof(WorldGameObject), "InitAllWorldWGOs")]
        [HarmonyPostfix]
        private static void InitAllWorldWGOs_Postfix()
        {
            WGORegistry.Instance?.RebuildNow();
            LiveWGOTransformSync.Instance?.ResetSessionState();
            HostAuthorityInteractionSync.Instance?.ResetSessionState();
        }
    }
}

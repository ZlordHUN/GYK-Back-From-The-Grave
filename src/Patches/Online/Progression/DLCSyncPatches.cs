using HarmonyLib;
using DLCRefugees;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch]
    internal static class DLCSyncPatches
    {
        [HarmonyPatch(typeof(RefugeesCampEngine), "StartCampLive")]
        [HarmonyPostfix]
        internal static void StartCampLive_Postfix()
        {
            MarkDirtyIfEnabled();
        }

        [HarmonyPatch(typeof(RefugeesCampEngine), "StopCampLive")]
        [HarmonyPostfix]
        internal static void StopCampLive_Postfix()
        {
            MarkDirtyIfEnabled();
        }

        [HarmonyPatch(typeof(RefugeesCampEngine), "SetCampMusic")]
        [HarmonyPostfix]
        internal static void SetCampMusic_Postfix()
        {
            MarkDirtyIfEnabled();
        }

        [HarmonyPatch(typeof(RefugeesCampEngine), "SetCampMusicAlarich")]
        [HarmonyPostfix]
        internal static void SetCampMusicAlarich_Postfix()
        {
            MarkDirtyIfEnabled();
        }

        [HarmonyPatch(typeof(RefugeesCampEngine), "UpdateRefugeeCampValues")]
        [HarmonyPostfix]
        internal static void UpdateRefugeeCampValues_Postfix()
        {
            MarkDirtyIfEnabled();
        }

        [HarmonyPatch(typeof(PlayersTavernEngine), "AddNewVisitor")]
        [HarmonyPostfix]
        internal static void AddNewVisitor_Postfix()
        {
            MarkDirtyIfEnabled();
        }

        [HarmonyPatch(typeof(PlayersTavernEngine), "RemoveVisitor")]
        [HarmonyPostfix]
        internal static void RemoveVisitor_Postfix()
        {
            MarkDirtyIfEnabled();
        }

        [HarmonyPatch(typeof(PlayersTavernEngine), "TemporarilyRemoveVisitors")]
        [HarmonyPostfix]
        internal static void TemporarilyRemoveVisitors_Postfix()
        {
            MarkDirtyIfEnabled();
        }

        [HarmonyPatch(typeof(PlayersTavernEngine), "PlaceVisitorsBackAfterEvent")]
        [HarmonyPostfix]
        internal static void PlaceVisitorsBackAfterEvent_Postfix()
        {
            MarkDirtyIfEnabled();
        }

        [HarmonyPatch(typeof(PlayersTavernEngine), "AddGDPoint")]
        [HarmonyPostfix]
        internal static void AddGDPoint_Postfix()
        {
            MarkDirtyIfEnabled();
        }

        private static void MarkDirtyIfEnabled()
        {
            var onlineCoop = GraveyardKeeperCoop.Network.OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled) return;
            var sync = GraveyardKeeperCoop.Multiplayer.DLCSync.Instance;
            if (sync == null) return;
            sync.MarkDirty();
        }
    }
}
using HarmonyLib;
using DungeonGenerator;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch]
    internal static class DungeonSyncPatches
    {
        [HarmonyPatch(typeof(MainGame), "TeleportToDungeonLevel", typeof(int))]
        [HarmonyPrefix]
        internal static void TeleportToDungeonLevel_Prefix(int level)
        {
            if (!IsSyncEnabled()) return;
            var sync = GraveyardKeeperCoop.Multiplayer.DungeonSync.Instance;
            if (sync == null) return;
            sync.SendDungeonEnter(level);
        }

        [HarmonyPatch(typeof(TextureDrawer), "TrySaveDungeon")]
        [HarmonyPostfix]
        internal static void TrySaveDungeon_Postfix(TextureDrawer __instance, bool __result)
        {
            if (!IsSyncEnabled()) return;
            if (!__result) return;
            if (__instance?.cur_dungeon_preset == null) return;
            var sync = GraveyardKeeperCoop.Multiplayer.DungeonSync.Instance;
            if (sync == null) return;
            sync.SendDungeonLevelState(__instance.cur_dungeon_preset.dungeon_level);
        }

        [HarmonyPatch(typeof(TextureDrawer), "UpdateDungeonState")]
        [HarmonyPostfix]
        internal static void UpdateDungeonState_Postfix(TextureDrawer __instance)
        {
            if (!IsSyncEnabled()) return;
            if (__instance?.cur_saved_dungeon == null || __instance?.cur_dungeon_preset == null) return;
            if (!__instance.cur_saved_dungeon.is_completed) return;
            var sync = GraveyardKeeperCoop.Multiplayer.DungeonSync.Instance;
            if (sync == null) return;
            sync.SendDungeonLevelCompleted(__instance.cur_dungeon_preset.dungeon_level);
        }

        private static bool IsSyncEnabled()
        {
            var onlineCoop = GraveyardKeeperCoop.Network.OnlineCoopManager.Instance;
            return onlineCoop != null && onlineCoop.IsOnlineCoopEnabled;
        }
    }
}
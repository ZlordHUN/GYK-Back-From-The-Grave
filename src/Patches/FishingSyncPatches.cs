using HarmonyLib;
using System.Collections.Generic;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch]
    internal static class FishingSyncPatches
    {
        private static int _knownFishesCountBefore;
        private static int _knownFishesClearCountBefore;

        [HarmonyPatch(typeof(FishingGUI), "UpdateTakingOut")]
        [HarmonyPrefix]
        internal static void UpdateTakingOut_Prefix()
        {
            if (!IsSyncEnabled()) return;
            if (MainGame.me?.save == null) return;

            _knownFishesCountBefore = MainGame.me.save.known_fishes?.Count ?? 0;
            _knownFishesClearCountBefore = MainGame.me.save.known_fishes_clear?.Count ?? 0;
        }

        [HarmonyPatch(typeof(FishingGUI), "UpdateTakingOut")]
        [HarmonyPostfix]
        internal static void UpdateTakingOut_Postfix()
        {
            if (!IsSyncEnabled()) return;
            if (MainGame.me?.save == null) return;

            int knownCountAfter = MainGame.me.save.known_fishes?.Count ?? 0;
            int clearCountAfter = MainGame.me.save.known_fishes_clear?.Count ?? 0;

            bool knownFishesGrew = knownCountAfter > _knownFishesCountBefore;
            bool clearFishesGrew = clearCountAfter > _knownFishesClearCountBefore;

            if (knownFishesGrew || clearFishesGrew)
            {
                var sync = GraveyardKeeperCoop.Multiplayer.FishingSync.Instance;
                if (sync == null) return;

                if (clearFishesGrew && MainGame.me.save.known_fishes_clear.Count > 0)
                {
                    string newClearName = MainGame.me.save.known_fishes_clear[MainGame.me.save.known_fishes_clear.Count - 1];
                    string newFishId = knownFishesGrew && MainGame.me.save.known_fishes.Count > 0
                        ? MainGame.me.save.known_fishes[MainGame.me.save.known_fishes.Count - 1]
                        : "";
                    sync.SendFishCaught(newFishId, newClearName);
                }
                else if (knownFishesGrew && MainGame.me.save.known_fishes.Count > 0)
                {
                    sync.SendFishCaught(MainGame.me.save.known_fishes[MainGame.me.save.known_fishes.Count - 1], "");
                }

                sync.MarkDirty();
            }
        }

        [HarmonyPatch(typeof(FishingGUI), "SaveLastBait")]
        [HarmonyPostfix]
        internal static void SaveLastBait_Postfix()
        {
            if (!IsSyncEnabled()) return;
            var sync = GraveyardKeeperCoop.Multiplayer.FishingSync.Instance;
            if (sync == null) return;
            sync.MarkDirty();
        }

        private static bool IsSyncEnabled()
        {
            var onlineCoop = GraveyardKeeperCoop.Network.OnlineCoopManager.Instance;
            return onlineCoop != null && onlineCoop.IsOnlineCoopEnabled;
        }
    }
}

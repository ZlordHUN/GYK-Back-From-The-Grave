using HarmonyLib;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches save system to handle multiplayer scenarios.
    /// Policy: in online co-op, BOTH players can save (each saves their own local slot),
    /// but only the HOST can load. A client loading a save would tear themselves out of
    /// the lobby and into a solo game-state owned only by them, which immediately desyncs
    /// the session. Blocking the load keeps the party together.
    /// </summary>
    [HarmonyPatch]
    public static class SaveSystemPatches
    {
        private sealed class SaveWriteState
        {
            public string DataPath;
            public bool Existed;
            public System.DateTime LastWriteUtc;
        }

        /// <summary>
        /// Block clients from LOADING a save via PlatformSpecific.LoadGame.
        /// Covers the pause-menu "Load Game" flow plus any other entry point
        /// (quick-load, gamepad shortcut, etc.).
        /// </summary>
        [HarmonyPatch(typeof(PlatformSpecific), "LoadGame")]
        [HarmonyPrefix]
        public static bool LoadGame_Prefix()
        {
            var onlineCoop = OnlineCoopManager.Instance;

            // Allow normal load if online coop is not active. This also permits the
            // client-side load of a save explicitly delivered by the host; that path
            // disables OnlineCoopManager before installing the received save.
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return true;

            // Block load for clients — host is the save-of-record while in a lobby
            if (!onlineCoop.IsHost)
            {
                CoopMod.Logger.LogInfo("[LoadBlock] Client load blocked — only the host can load saves while in online co-op");
                TryShowClientLoadBlockedMessage();
                return false; // Skip original load method
            }

            return true;
        }

        /// <summary>
        /// Block the SaveSlotsMenuGUI "select a slot to load" path specifically, so even
        /// if some code path bypasses PlatformSpecific.LoadGame we still stop the client
        /// before scene transition begins. Also gives us a user-visible notice.
        /// </summary>
        [HarmonyPatch(typeof(SaveSlotsMenuGUI), "OnLoadSlot")]
        [HarmonyPriority(Priority.High)]
        [HarmonyPrefix]
        public static bool OnLoadSlot_Prefix()
        {
            var onlineCoop = OnlineCoopManager.Instance;

            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return true;

            if (!onlineCoop.IsHost)
            {
                CoopMod.Logger.LogInfo("[LoadBlock] Client OnLoadSlot blocked — only the host can load saves while in online co-op");
                TryShowClientLoadBlockedMessage();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Capture the old file timestamp because vanilla catches save I/O failures and
        /// still invokes its completion callback. The postfix must not commit joiner state
        /// against an unchanged stale .dat after a failed write.
        /// </summary>
        [HarmonyPatch(typeof(PlatformSpecific), "SaveGameDataToSlot")]
        [HarmonyPrefix]
        private static void SaveGameDataToSlot_Prefix(
            SaveSlotData slot,
            out SaveWriteState __state)
        {
            __state = new SaveWriteState();
            OnlineCoopManager coop = OnlineCoopManager.Instance;
            if (coop == null || !coop.IsOnlineCoopEnabled || !coop.IsHost ||
                !MainGame.game_started)
            {
                return;
            }

            if (slot == null || string.IsNullOrEmpty(slot.filename_no_extension))
                return;

            try
            {
                __state.DataPath = PlatformSpecific.GetSaveFolder() +
                                   slot.filename_no_extension + ".dat";
                __state.Existed = System.IO.File.Exists(__state.DataPath);
                if (__state.Existed)
                    __state.LastWriteUtc = System.IO.File.GetLastWriteTimeUtc(__state.DataPath);
            }
            catch (System.Exception ex)
            {
                __state.DataPath = null;
                CoopMod.Logger.LogWarning(
                    $"[JoinerProfile] Could not inspect host save before write: {ex.Message}");
            }
        }

        /// <summary>
        /// Publish the committed .dat revision. Joiners use this as the only transaction
        /// boundary for personal inventory, loadout, and money persistence.
        /// </summary>
        [HarmonyPatch(typeof(PlatformSpecific), "SaveGameDataToSlot")]
        [HarmonyPostfix]
        private static void SaveGameDataToSlot_Postfix(
            SaveSlotData slot,
            SaveWriteState __state)
        {
            try
            {
                if (__state == null || string.IsNullOrEmpty(__state.DataPath) ||
                    !System.IO.File.Exists(__state.DataPath))
                {
                    Multiplayer.SaveTransferManager.NotifyHostSaveWriteCompleted(
                        slot,
                        false);
                    return;
                }

                if (__state.Existed &&
                    System.IO.File.GetLastWriteTimeUtc(__state.DataPath) == __state.LastWriteUtc)
                {
                    CoopMod.Logger.LogWarning(
                        "[JoinerProfile] Host save file did not change; personal profile " +
                        "commit was withheld in case the save write failed");
                    Multiplayer.SaveTransferManager.NotifyHostSaveWriteCompleted(
                        slot,
                        false);
                    return;
                }

                Multiplayer.JoinerProfileManager.NotifyHostSaveCompleted(slot);
                Multiplayer.SaveTransferManager.NotifyHostSaveWriteCompleted(
                    slot,
                    true);
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[JoinerProfile] Could not verify completed host save: {ex.Message}");
                Multiplayer.SaveTransferManager.NotifyHostSaveWriteCompleted(
                    slot,
                    false);
            }
        }

        private static void TryShowClientLoadBlockedMessage()
        {
            // The pause-menu Load Game button is hidden for clients (see InGameMenuPatches),
            // so this path is only hit if some code bypasses the UI. Logging is enough for now.
            CoopMod.Logger.LogWarning("[LoadBlock] A load was attempted by a client — ignoring.");
        }
    }
}

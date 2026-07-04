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

            // Allow normal load if online coop is not active
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

        private static void TryShowClientLoadBlockedMessage()
        {
            // The pause-menu Load Game button is hidden for clients (see InGameMenuPatches),
            // so this path is only hit if some code bypasses the UI. Logging is enough for now.
            CoopMod.Logger.LogWarning("[LoadBlock] A load was attempted by a client — ignoring.");
        }
    }
}

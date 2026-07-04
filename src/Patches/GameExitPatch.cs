using HarmonyLib;
using System;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Disconnect clients only when the host ACTUALLY leaves the game.
    /// OnPressedSaveAndExit merely opens a Yes/No confirmation dialog; patching it would
    /// boot clients the moment the host clicks the button, even if they then pick "No".
    /// ReturnToMainMenu runs only from the dialog's "Yes" callback, so this fires solely
    /// on a confirmed exit.
    /// </summary>
    [HarmonyPatch(typeof(InGameMenuGUI), "ReturnToMainMenu")]
    public static class InGameExitPatch
    {
        static void Prefix()
        {
            try
            {
                CoopMod.Logger.LogInfo("Host confirmed exit to main menu - disconnecting clients");

                // Clear the multiplayer-session flag FIRST so the auto-enable loop in
                // OnlineCoopManager.Update() doesn't immediately re-enable coop while
                // we're in the middle of returning to the main menu.
                // (Observed bug: "Online co-op disabled" was instantly followed by
                //  "Auto-enabling online coop" because MemberCount was still 2.)
                GraveyardKeeperCoop.UI.LobbyGUI.ClearMultiplayerSession();

                // Disable online co-op so the disconnect detection loop doesn't fire
                // "Lost connection to host" after we've already voluntarily left
                if (GraveyardKeeperCoop.Network.OnlineCoopManager.Instance != null &&
                    GraveyardKeeperCoop.Network.OnlineCoopManager.Instance.IsOnlineCoopEnabled)
                {
                    CoopMod.Logger.LogInfo("Disabling online co-op before exit...");
                    GraveyardKeeperCoop.Network.OnlineCoopManager.Instance.DisableOnlineCoop();
                }

                // Clean up the chat overlay if it exists
                if (GraveyardKeeperCoop.UI.InGameChatOverlay.Instance != null)
                {
                    GraveyardKeeperCoop.UI.InGameChatOverlay.Instance.OnReturnToMenu();
                }

                GraveyardKeeperCoop.UI.ChatOverlay.Instance?.Hide();

                // CRITICAL: Always leave the Steam lobby directly here. Previously this
                // was only invoked via InGameChatOverlay.OnReturnToMenu(), which silently
                // skipped LeaveLobby() when Instance was null. When we don't leave the
                // Steam lobby, the other player never receives a LobbyChatUpdate_t
                // callback and therefore never sees the "host disconnected" dialog.
                try
                {
                    var lobbyMgr = GraveyardKeeperCoop.Network.SteamLobbyManager.Instance;
                    if (lobbyMgr != null && lobbyMgr.IsInLobby)
                    {
                        CoopMod.Logger.LogInfo("[GameExit] Leaving Steam lobby so remote player gets disconnect notification");
                        lobbyMgr.LeaveLobby();
                    }
                }
                catch (Exception lobbyEx)
                {
                    CoopMod.Logger.LogError($"[GameExit] Error leaving lobby on exit: {lobbyEx.Message}");
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"Error in InGameExitPatch: {ex.Message}");
            }
        }
    }
}

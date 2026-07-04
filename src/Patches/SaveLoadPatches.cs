using HarmonyLib;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches to fix save/load position issues
    /// </summary>
    [HarmonyPatch(typeof(WorldGameObject))]
    public class SaveLoadPatches
    {
        private static bool skipNextRespawnTeleport = false;
        private static bool immediateSceneClearForNextLoad = false;

        public static void MarkInGameLoadStarting()
        {
            immediateSceneClearForNextLoad = true;
            CoopMod.Logger.LogInfo("[SaveLoadPatch] Marked next scene restore for immediate in-game clear");
        }

        /// <summary>
        /// Intercept TeleportToGDPoint to prevent respawn teleport after loading a save
        /// </summary>
        [HarmonyPatch("TeleportToGDPoint")]
        [HarmonyPrefix]
        public static bool TeleportToGDPoint_Prefix(WorldGameObject __instance, string gd_point_tag, bool dont_move_camera_while_tp)
        {
            // Only intercept teleports to respawn points for the player
            if (__instance.is_player && gd_point_tag != null && gd_point_tag.StartsWith("gd_player_respawn"))
            {
                // Check if we should skip this teleport (after loading a save)
                if (skipNextRespawnTeleport)
                {
                    CoopMod.Logger.LogInfo($"[SaveLoadPatch] Blocked respawn teleport to '{gd_point_tag}' - using saved position instead");
                    skipNextRespawnTeleport = false;
                    return false; // Skip the teleport
                }
            }

            // Allow normal teleports
            return true;
        }

        /// <summary>
        /// After a save is loaded, set flag to skip the next respawn teleport
        /// </summary>
        [HarmonyPatch(typeof(SaveSlotsMenuGUI), "OnLoadSlot")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Low)]
        public static void OnLoadSlot_Prefix(SaveSlotData slot)
        {
            // Skip if online co-op client — SaveSystemPatches blocks these at High priority.
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop != null && onlineCoop.IsOnlineCoopEnabled && !onlineCoop.IsHost)
                return;

            if (slot != null)
            {
                CoopMod.Logger.LogInfo($"[SaveLoadPatch] Loading save slot: {slot.real_time}");
                // Set flag to skip the respawn teleport that happens after loading
                skipNextRespawnTeleport = true;
                MarkHostedLobbyAsRunning();

                TryTriggerHostHotReload(slot);
            }
        }

        private static void MarkHostedLobbyAsRunning()
        {
            try
            {
                var lobby = Network.SteamLobbyManager.Instance;
                if (lobby != null && lobby.IsInLobby && lobby.IsHost)
                {
                    lobby.MarkGameRunning();
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[SaveLoadPatch] Failed to mark lobby as running: {ex.Message}");
            }
        }

        /// <summary>
        /// If the HOST initiates an in-game Load while online co-op is active, broadcast
        /// the new save selection to every client, reset the transfer manager with the
        /// new save, and tear down the local coop state so that when our player respawns
        /// after the load the normal spawn-hook re-enables coop against the same lobby.
        /// Clients mirror the reload via the existing GAME_START path (LobbyGUI receives
        /// it and calls back into <see cref="Network.OnlineCoopManager.PrepareHotReload"/>
        /// before requesting the save).
        /// </summary>
        private static void TryTriggerHostHotReload(SaveSlotData slot)
        {
            try
            {
                var coop = Network.OnlineCoopManager.Instance;
                if (coop == null || !coop.IsOnlineCoopEnabled || !coop.IsHost)
                    return;

                var lobby = Network.SteamLobbyManager.Instance;
                int memberCount = lobby != null ? lobby.GetLobbyMemberCount() : 1;
                if (memberCount <= 1)
                {
                    // No clients to sync to — fall back to local reset only.
                    CoopMod.Logger.LogInfo("[SaveLoadPatch] Host hot-reload: no clients in lobby, local reset only");
                    coop.PrepareHotReload();
                    return;
                }

                CoopMod.Logger.LogInfo($"[SaveLoadPatch] Host hot-reload: notifying {memberCount - 1} client(s) to reload save '{slot.filename_no_extension}'");

                // 1) Register the new save so any SaveRequest (from clients) returns THIS save.
                Multiplayer.SaveTransferManager.SetHostSaveSlot(slot);

                // 2) Tear down our own coop state; it will be rebuilt on respawn.
                // Do this BEFORE arming GameLoadSync, because PrepareHotReload resets
                // stale sync state from the previous world.
                coop.PrepareHotReload();

                // 3) Arm GameLoadSync for the new load cycle (expect both players again).
                Multiplayer.GameLoadSync.Instance?.StartWaitingForPlayers(memberCount);

                // 4) Tell clients to reload. They receive this via LobbyGUI.OnGameStartReceived.
                string saveSlotName = slot.real_time ?? slot.filename_no_extension ?? "";
                Network.SteamP2PManager.Instance?.BroadcastGameStart(saveSlotName);
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[SaveLoadPatch] TryTriggerHostHotReload failed: {ex}");
            }
        }

        /// <summary>
        /// The base game was not designed for our pause-menu in-game load path. Its
        /// ClearSceneMap uses Destroy(), then RestoreScene immediately instantiates
        /// the save contents in the same frame. In an already-running world that leaves
        /// the old WGOs alive until frame end and doubles the map. For an in-game load,
        /// clear the scene synchronously instead.
        /// </summary>
        [HarmonyPatch(typeof(SerializableGameMap), "ClearSceneMap")]
        [HarmonyPrefix]
        public static bool ClearSceneMap_Prefix()
        {
            if (!immediateSceneClearForNextLoad)
                return true;

            immediateSceneClearForNextLoad = false;

            CoopMod.Logger.LogInfo("[SaveLoadPatch] In-game load: clearing current scene immediately before restore");

            SaveSlotsMenuGUI.PrepareScene();

            if (MainGame.me?.world_root != null)
            {
                WorldGameObject[] wgos = MainGame.me.world_root.GetComponentsInChildren<WorldGameObject>(true);
                for (int i = 0; i < wgos.Length; i++)
                {
                    WorldGameObject wgo = wgos[i];
                    if (wgo == null || wgo.GetComponent<PlayerComponent>() != null)
                        continue;

                    if (wgo.name.Contains("Player"))
                    {
                        CoopMod.Logger.LogError("Deleting player-like WGO during immediate clear: " + wgo.name);
                    }

                    Object.DestroyImmediate(wgo.gameObject);
                }

                SimplifiedWGO[] simplified = MainGame.me.world_root.GetComponentsInChildren<SimplifiedWGO>(true);
                for (int i = 0; i < simplified.Length; i++)
                {
                    if (simplified[i] != null)
                    {
                        Object.DestroyImmediate(simplified[i].gameObject);
                    }
                }

                ChunkManager.ClearChunksList();

                ChunkedGameObject[] chunked = MainGame.me.world_root.GetComponentsInChildren<ChunkedGameObject>(true);
                for (int i = 0; i < chunked.Length; i++)
                {
                    if (chunked[i] != null)
                    {
                        chunked[i].ResetAtTheBeginning();
                    }
                }
            }

            return false;
        }
    }
}

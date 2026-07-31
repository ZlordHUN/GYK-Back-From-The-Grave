using System;
using System.Collections.Generic;
using Steamworks;

namespace GraveyardKeeperCoop.Network
{
    /// <summary>
    /// Serializable representation of a single save slot as sent over the wire.
    /// Intentionally avoids dependency on the game's SaveSlotData type so it can be
    /// constructed on the host (from SaveSlotData) and rebuilt on the client as
    /// display-only data (no thumbnails, no linked save reference).
    /// </summary>
    public struct HostSaveEntryWire
    {
        public string Filename;  // filename_no_extension; "" means synthetic "New Game" slot
        public string RealTime;  // e.g. "12:34, 03 Mar 2025"
        public string Stats;     // e.g. "(wskull)1.2  (cross)3.4"
        public float GameTime;   // hours played
        public float Version;    // save format version
    }

    /// <summary>
    /// Mirrors the host's lobby-screen save slot list to clients so that they can
    /// see (read-only) exactly what the host sees, including which slot is currently
    /// selected. Clients do NOT see their own local saves in the lobby; they see the
    /// host's list. The host sees their own real saves as usual.
    ///
    /// Flow:
    ///   - Client joins lobby -> sends HostSaveListRequest to host.
    ///   - Host receives request -> reads local saves via PlatformSpecific and replies
    ///     with HostSaveList (entries + currently-selected filename).
    ///   - When host picks a slot in their lobby UI, host broadcasts HostSaveSelected
    ///     so everyone updates their highlight.
    /// </summary>
    public static class HostSaveListSync
    {
        private static bool _initialized;

        /// <summary>
        /// Last-known list of the host's save slots (empty on the host itself and
        /// before any HostSaveList message has arrived on clients).
        /// </summary>
        public static List<HostSaveEntryWire> HostSlots { get; private set; } = new List<HostSaveEntryWire>();

        /// <summary>
        /// filename_no_extension of the slot the host currently has highlighted, or
        /// "" for New Game. Null means the host has not selected anything.
        /// </summary>
        public static string HostSelectedFilename { get; private set; }

        /// <summary>
        /// Fired on the client whenever HostSlots or HostSelectedFilename changes,
        /// so UI panels can refresh themselves.
        /// </summary>
        public static event Action OnUpdated;

        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;

            // Client subscribes: receive list + selection updates from host.
            SteamP2PManager.Instance.OnHostSaveListReceived += OnHostSaveListReceived;
            SteamP2PManager.Instance.OnHostSaveSelectedReceived += OnHostSaveSelectedReceived;

            // Host subscribes: respond to list requests from joining clients.
            SteamP2PManager.Instance.OnHostSaveListRequested += OnHostSaveListRequestedByClient;

            // Client: when we finish joining a lobby, proactively ask the host for the list.
            if (SteamLobbyManager.Instance != null)
            {
                SteamLobbyManager.Instance.OnLobbyJoined += OnLocalLobbyJoined;
            }

            CoopMod.Logger.LogInfo("[HostSaveListSync] Initialized");
        }

        private static void OnLocalLobbyJoined(CSteamID lobbyID)
        {
            // Reset any stale state from previous sessions.
            HostSlots = new List<HostSaveEntryWire>();
            HostSelectedFilename = null;
            _hostLocallySelected = null;

            if (SteamLobbyManager.Instance == null) return;

            if (SteamLobbyManager.Instance.IsHost)
            {
                // Host does not mirror anyone - they show their own real saves.
                // We still fire OnUpdated so any panel waiting for data clears its "Waiting..." state.
                CoopMod.Logger.LogInfo("[HostSaveListSync] Local user is host - not requesting save list");
                SafeFireUpdated();
                return;
            }

            CoopMod.Logger.LogInfo("[HostSaveListSync] Local user joined as client - requesting host save list");
            // Small safety: if request send fails (P2P session not yet up), we'll still
            // get served once the host receives our first reliable message / ping.
            try { SteamP2PManager.Instance.SendHostSaveListRequest(); }
            catch (Exception ex) { CoopMod.Logger.LogWarning($"[HostSaveListSync] SendHostSaveListRequest failed: {ex.Message}"); }

            SafeFireUpdated();
        }

        private static void OnHostSaveListReceived(CSteamID senderID, List<HostSaveEntryWire> entries, string selectedFilename)
        {
            HostSlots = entries ?? new List<HostSaveEntryWire>();
            HostSelectedFilename = selectedFilename;
            CoopMod.Logger.LogInfo(
                $"[HostSaveListSync] Updated: {HostSlots.Count} entries, " +
                $"selected='{HostSelectedFilename ?? "<none>"}'");
            SafeFireUpdated();
        }

        private static void OnHostSaveSelectedReceived(CSteamID senderID, string selectedFilename)
        {
            HostSelectedFilename = selectedFilename ?? "";
            CoopMod.Logger.LogInfo($"[HostSaveListSync] Host selection changed: '{HostSelectedFilename}'");
            SafeFireUpdated();
        }

        private static void OnHostSaveListRequestedByClient(CSteamID requester)
        {
            if (SteamLobbyManager.Instance == null || !SteamLobbyManager.Instance.IsHost)
            {
                CoopMod.Logger.LogWarning("[HostSaveListSync] Got HostSaveListRequest but we are not host - ignoring");
                return;
            }

            BuildHostSaveList((entries, currentSelected) =>
            {
                SteamP2PManager.Instance.SendHostSaveList(requester, entries, currentSelected);
            });
        }

        /// <summary>
        /// Re-send the host's current co-op save list after a local slot changes.
        /// </summary>
        public static void HostBroadcastCurrentList()
        {
            var lobbyManager = SteamLobbyManager.Instance;
            if (lobbyManager == null || !lobbyManager.IsHost || !lobbyManager.IsInLobby)
                return;

            BuildHostSaveList((entries, currentSelected) =>
            {
                CSteamID lobbyID = lobbyManager.CurrentLobbyID;
                CSteamID localID = SteamUser.GetSteamID();
                int memberCount = SteamMatchmaking.GetNumLobbyMembers(lobbyID);

                for (int i = 0; i < memberCount; i++)
                {
                    CSteamID memberID = SteamMatchmaking.GetLobbyMemberByIndex(lobbyID, i);
                    if (memberID != CSteamID.Nil && memberID != localID)
                        SteamP2PManager.Instance.SendHostSaveList(memberID, entries, currentSelected);
                }
            });
        }

        private static void BuildHostSaveList(Action<List<HostSaveEntryWire>, string> onReady)
        {
            PlatformSpecific.ReadSaveSlots(slots =>
            {
                try
                {
                    var entries = new List<HostSaveEntryWire>
                    {
                        CreateNewGameEntry()
                    };

                    if (slots != null)
                    {
                        foreach (var s in slots)
                        {
                            if (s == null) continue;
                            if (!Patches.MainMenuPatches.IsMultiplayerSave(s)) continue;
                            // Skip empty slots (match SaveSelectorPanel behavior)
                            if (s.game_time <= 0.01f) continue;
                            entries.Add(new HostSaveEntryWire
                            {
                                Filename = s.filename_no_extension ?? "",
                                RealTime = s.real_time ?? "",
                                Stats = s.stats ?? "",
                                GameTime = s.game_time,
                                Version = s.version,
                            });
                        }
                    }

                    string currentSelected = GetHostCurrentlySelectedFilename();
                    onReady?.Invoke(entries, currentSelected);
                }
                catch (Exception ex)
                {
                    CoopMod.Logger.LogError($"[HostSaveListSync] Failed to build HostSaveList: {ex}");
                }
            });
        }

        private static HostSaveEntryWire CreateNewGameEntry()
        {
            return new HostSaveEntryWire
            {
                Filename = "",
                RealTime = "",
                Stats = "",
                GameTime = 0f,
                Version = 0f,
            };
        }

        /// <summary>
        /// Called on the host to broadcast that they just picked a different slot
        /// in their own lobby save selector.
        /// </summary>
        public static void HostBroadcastSelected(string filenameNoExtension)
        {
            if (SteamLobbyManager.Instance == null || !SteamLobbyManager.Instance.IsHost) return;
            try
            {
                SteamP2PManager.Instance.BroadcastHostSaveSelected(filenameNoExtension ?? "");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[HostSaveListSync] BroadcastHostSaveSelected failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Best-effort lookup of whatever filename the host has currently highlighted
        /// in their lobby UI. LobbyGUI updates this via HostBroadcastSelected -> we
        /// remember it here too so late-joining clients see the right selection.
        /// </summary>
        private static string _hostLocallySelected;
        public static void HostRememberLocalSelection(string filenameNoExtension)
        {
            // This method is called only after an actual click. Empty therefore
            // unambiguously means the host explicitly selected New Game.
            _hostLocallySelected = filenameNoExtension ?? "";
        }
        private static string GetHostCurrentlySelectedFilename() => _hostLocallySelected;

        private static void SafeFireUpdated()
        {
            try { OnUpdated?.Invoke(); }
            catch (Exception ex) { CoopMod.Logger.LogWarning($"[HostSaveListSync] OnUpdated handler threw: {ex.Message}"); }
        }
    }
}

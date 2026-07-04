using BepInEx.Configuration;

namespace GraveyardKeeperCoop
{
    public class ModConfig
    {
        // Singleton for easy access
        public static ModConfig Instance { get; private set; }

        // ===== Network Settings =====
        public static ConfigEntry<string> ServerIP;
        public static ConfigEntry<int> ServerPort;
        public static ConfigEntry<bool> IsHost;
        public static ConfigEntry<int> MaxPlayers;
        public static ConfigEntry<SessionVisibility> HostedSessionVisibility;
        public static ConfigEntry<string> FavoriteServerHostIds;
        public static ConfigEntry<string> FavoriteServerNames;
        public static ConfigEntry<int> NetworkTickRate;
        public static ConfigEntry<bool> EnableLiveWGOStateSync;
        public static ConfigEntry<bool> EnablePlayerVisualSync;
        public static ConfigEntry<bool> EnableNpcVisualSync;
        public static ConfigEntry<bool> EnableLiveWGOTransformSync;
        public static ConfigEntry<bool> EnableHostAuthorityInteractions;
        public static ConfigEntry<string> HostAuthorityObjectIds;
        public static ConfigEntry<bool> EnableJoinerProfilePersistence;
        public static ConfigEntry<bool> EnableNetworkDebugOverlay;
        public static ConfigEntry<bool> EnableSaveDiagnostics;
        public static ConfigEntry<bool> EnableLocalMotionSmoothing;
        public static ConfigEntry<bool> OverrideFramePacing;
        public static ConfigEntry<int> TargetFrameRate;

        // ===== Local Co-op Settings =====
        public static ConfigEntry<bool> EnableLocalCoop;
        public static ConfigEntry<CameraMode> LocalCoopCameraMode;

        // ===== Dialogue to Chat Settings =====
        public static ConfigEntry<bool> EnableDialogueToChat;
        public static ConfigEntry<bool> ShowNPCDialogue;
        public static ConfigEntry<bool> ShowPlayerChoices;

        // ===== Chat Settings =====
        public static ConfigEntry<ChatMode> InGameChatMode;
        public static ConfigEntry<bool> ShowChatBubbles;
        public static ConfigEntry<float> ChatBubbleDuration;

        // ===== DLC Settings =====
        public static ConfigEntry<bool> EnableDLCStories;
        public static ConfigEntry<bool> EnableDLCRefugees;
        public static ConfigEntry<bool> EnableDLCSouls;
        public static ConfigEntry<bool> EnableCheats;

        // ===== Player Cosmetics =====
        public ConfigEntry<int> PlayerPantsColor;
        public ConfigEntry<int> PlayerShirtColor;
        public ConfigEntry<int> PlayerHairColor;
        public ConfigEntry<int> PlayerSkinTone;
        public ConfigEntry<int> PlayerEyeColor;

        public enum CameraMode
        {
            Shared,      // Single camera following midpoint between players (WORKING)
            SplitScreen  // Split screen mode (NOT IMPLEMENTED - for future)
        }

        public enum ChatMode
        {
            Overlay,     // In-game chat overlay (GYK style) - always visible, press Enter to type
            Button       // Chat button in multiplayer menu - opens chat panel
        }

        public enum SessionVisibility
        {
            Public,
            Friends,
            Private
        }

        public static string GetDLCRequirementsString()
        {
            var parts = new System.Collections.Generic.List<string>();
            if (EnableDLCStories.Value) parts.Add("stories");
            if (EnableDLCRefugees.Value) parts.Add("refugees");
            if (EnableDLCSouls.Value) parts.Add("souls");
            return string.Join(",", parts);
        }

        public static bool CheckDLCAvailability(out string missingDLCs)
        {
            var missing = new System.Collections.Generic.List<string>();
            if (EnableDLCStories.Value && !DLCEngine.IsDLCAvailable(DLCEngine.DLCVersion.Stories))
                missing.Add("Stranger Sins");
            if (EnableDLCRefugees.Value && !DLCEngine.IsDLCAvailable(DLCEngine.DLCVersion.Refugees))
                missing.Add("Game of Crone");
            if (EnableDLCSouls.Value && !DLCEngine.IsDLCAvailable(DLCEngine.DLCVersion.Souls))
                missing.Add("Better Save Soul");
            missingDLCs = string.Join(", ", missing);
            return missing.Count == 0;
        }

        public static bool IsDLCRequired(string dlcId)
        {
            switch (dlcId)
            {
                case "stories": return EnableDLCStories.Value;
                case "refugees": return EnableDLCRefugees.Value;
                case "souls": return EnableDLCSouls.Value;
                default: return false;
            }
        }

        public static bool HasRequiredDLCs(string hostRequirements, out string missingDLCs)
        {
            var missing = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(hostRequirements)) { missingDLCs = ""; return true; }
            var required = hostRequirements.Split(',');
            foreach (var req in required)
            {
                string dlc = req.Trim();
                switch (dlc)
                {
                    case "stories":
                        if (!DLCEngine.IsDLCAvailable(DLCEngine.DLCVersion.Stories))
                            missing.Add("Stranger Sins");
                        break;
                    case "refugees":
                        if (!DLCEngine.IsDLCAvailable(DLCEngine.DLCVersion.Refugees))
                            missing.Add("Game of Crone");
                        break;
                    case "souls":
                        if (!DLCEngine.IsDLCAvailable(DLCEngine.DLCVersion.Souls))
                            missing.Add("Better Save Soul");
                        break;
                }
            }
            missingDLCs = string.Join(", ", missing);
            return missing.Count == 0;
        }

        public static void InitConfig(ConfigFile config)
        {
            // ===== Network Settings =====
            IsHost = config.Bind("Network",
                "IsHost",
                false,
                "Is this player the host?");

            ServerIP = config.Bind("Network",
                "ServerIP",
                "",
                "Server IP address to connect to (if not host)");

            ServerPort = config.Bind("Network",
                "ServerPort",
                7777,
                "Server port");

            MaxPlayers = config.Bind("Network",
                "MaxPlayers",
                2,
                "Maximum number of players (2-4 recommended)");

            HostedSessionVisibility = config.Bind("Network",
                "HostedSessionVisibility",
                SessionVisibility.Friends,
                "Hosted session visibility: Public (visible to everyone), Friends (visible to Steam friends), or Private (invite only)");

            FavoriteServerHostIds = config.Bind("Network",
                "FavoriteServerHostIds",
                "",
                "Comma-separated Steam host IDs saved from the server browser Favorites action.");

            FavoriteServerNames = config.Bind("Network",
                "FavoriteServerNames",
                "",
                "Semicolon-separated favorite server display names keyed by Steam host ID.");

            NetworkTickRate = config.Bind("Network",
                "NetworkTickRate",
                30,
                "Network update rate in Hz (10-60, default 30)");

            EnableLiveWGOStateSync = config.Bind("Performance",
                "EnableLiveWGOStateSyncOptimized",
                true,
                "Enable live full-world WGO state reconciliation after the initial save transfer. Disable if it causes performance issues while this sync path is optimized.");

            EnablePlayerVisualSync = config.Bind("Network",
                "EnablePlayerVisualSync",
                true,
                "Enable high-frequency player sprite/child-transform visual deltas for tools, carried items, and animation presentation.");

            EnableNpcVisualSync = config.Bind("Network",
                "EnableNpcVisualSync",
                true,
                "Enable host-driven NPC position and sprite visual sync. Disable if it fights local NPC AI/pathing on clients.");

            EnableLiveWGOTransformSync = config.Bind("Network",
                "EnableLiveWGOTransformSync",
                true,
                "Enable live transform smoothing for non-player physics-backed world objects between full WGO snapshots.");

            EnableHostAuthorityInteractions = config.Bind("Network",
                "EnableHostAuthorityInteractions",
                true,
                "Relay high-risk world interactions to the host instead of letting clients mutate those objects locally first.");

            HostAuthorityObjectIds = config.Bind("Network",
                "HostAuthorityObjectIds",
                "graved_skull",
                "Comma-separated WorldGameObject obj_id values that should be host-authoritative.");

            EnableJoinerProfilePersistence = config.Bind("Network",
                "EnableJoinerProfilePersistence",
                true,
                "Preserve a joiner's personal player state after loading the host's save and refresh it during client sessions.");

            EnableNetworkDebugOverlay = config.Bind("Debug",
                "EnableNetworkDebugOverlay",
                true,
                "Enable the F4 in-game network diagnostics overlay.");

            EnableSaveDiagnostics = config.Bind("Debug",
                "EnableSaveDiagnostics",
                false,
                "Enable verbose save/load diagnostic logging. This is intended for debugging only and is off by default.");

            EnableLocalMotionSmoothing = config.Bind("Performance",
                "EnableLocalMotionSmoothing",
                true,
                "Experimental: use Rigidbody2D Interpolate for the local player instead of the game's Extrapolate setting to reduce microstutter from physics/camera frame pacing.");

            OverrideFramePacing = config.Bind("Performance",
                "OverrideFramePacing",
                false,
                "Experimental: override the game's forced vSync setting every frame. Try enabling this if FPS is high but frame pacing still stutters.");

            TargetFrameRate = config.Bind("Performance",
                "TargetFrameRate",
                60,
                "Target FPS used when OverrideFramePacing is enabled. Use 60 for stable pacing, or your monitor refresh rate if externally synced.");

            // ===== Local Co-op Settings =====
            EnableLocalCoop = config.Bind("LocalCoop",
                "EnableLocalCoop",
                false,
                "Enable local co-op (second player joins on same PC)");

            LocalCoopCameraMode = config.Bind("LocalCoop",
                "CameraMode",
                CameraMode.Shared,
                "Camera mode: Shared (single camera follows both players - WORKING) or SplitScreen (not implemented yet)");

            // ===== Dialogue to Chat Settings =====
            EnableDialogueToChat = config.Bind("DialogueToChat",
                "EnableDialogueToChat",
                true,
                "Enable capturing and broadcasting dialogue to multiplayer chat");

            ShowNPCDialogue = config.Bind("DialogueToChat",
                "ShowNPCDialogue",
                true,
                "Show NPC dialogue in chat");

            ShowPlayerChoices = config.Bind("DialogueToChat",
                "ShowPlayerChoices",
                true,
                "Show player dialogue choices in chat");

            // ===== Chat Settings =====
            InGameChatMode = config.Bind("Chat",
                "InGameChatMode",
                ChatMode.Overlay,
                "Chat mode: Overlay (always visible in-game, press Enter to type) or Button (chat button in multiplayer menu)");

            ShowChatBubbles = config.Bind("Chat",
                "ShowChatBubbles",
                true,
                "Show chat messages as speech bubbles above players' heads");

            ChatBubbleDuration = config.Bind("Chat",
                "ChatBubbleDuration",
                5f,
                "How long chat bubbles stay visible (in seconds)");

            // ===== DLC Settings =====
            EnableDLCStories = config.Bind("DLC",
                "EnableDLCStories",
                true,
                "Enable Stranger Sins DLC sync (tavern visitors, idle points). Requires the DLC to be installed. Disable if the host or client doesn't have this DLC.");

            EnableDLCRefugees = config.Bind("DLC",
                "EnableDLCRefugees",
                true,
                "Enable Game of Crone DLC sync (refugee camp state). Requires the DLC to be installed. Disable if the host or client doesn't have this DLC.");

            EnableCheats = config.Bind("Cheats",
                "EnableCheats",
                false,
                "Enable debug commands (/give, /time, /weather) for all players in multiplayer.");

            EnableDLCSouls = config.Bind("DLC",
                "EnableDLCSouls",
                true,
                "Enable Better Save Soul DLC sync (soul zone WGOs handled by WGOStateSync). Requires the DLC to be installed. Disable if the host or client doesn't have this DLC.");

            // ===== Player Cosmetics =====
            Instance = new ModConfig();

            Instance.PlayerPantsColor = config.Bind("Cosmetics",
                "PantsColor",
                0,
                "Pants color index (0-7)");

            Instance.PlayerShirtColor = config.Bind("Cosmetics",
                "ShirtColor",
                0,
                "Shirt color index (0-7)");

            Instance.PlayerHairColor = config.Bind("Cosmetics",
                "HairColor",
                0,
                "Hair color index (0-7)");

            Instance.PlayerSkinTone = config.Bind("Cosmetics",
                "SkinTone",
                0,
                "Skin tone index (0-5)");

            Instance.PlayerEyeColor = config.Bind("Cosmetics",
                "EyeColor",
                0,
                "Eye color index (0-7)");
        }
    }
}

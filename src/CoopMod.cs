using System;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using GraveyardKeeperCoop.Network;
using GraveyardKeeperCoop.LocalCoop;
using GraveyardKeeperCoop.Utils;

namespace GraveyardKeeperCoop
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class CoopMod : BaseUnityPlugin
    {
        internal static new ManualLogSource Logger;
        internal static CoopMod Instance;
        
        // Local co-op manager
        private GameObject localCoopManagerObj;
        
        // Online co-op manager
        private GameObject onlineCoopManagerObj;
        
        private void Awake()
        {
            Instance = this;
            Logger = base.Logger;
            
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
            // Build stamp: the first thing to check in any log, so a stale DLL never eats a test.
            Logger.LogInfo($"Build: {System.IO.File.GetLastWriteTime(System.Reflection.Assembly.GetExecutingAssembly().Location):yyyy-MM-dd HH:mm:ss}");
            Logger.LogInfo("Graveyard Keeper already has UNET networking infrastructure!");
            
            // Initialize config
            ModConfig.InitConfig(base.Config);
            
            // Apply Harmony patches
            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll();
            
            Logger.LogInfo("Harmony patches applied");
            
            // Log all patched methods for debugging
            var patchedMethods = harmony.GetPatchedMethods().ToList();
            Logger.LogInfo($"Total patched methods: {patchedMethods.Count}");
            foreach (var method in patchedMethods)
            {
                Logger.LogInfo($"  - Patched {method.DeclaringType?.Name}.{method.Name}");
            }
            
            // Verify animation diagnostic patch targets
            try
            {
                Logger.LogInfo("[ANIM DIAG] Verifying patch targets...");
                var bccType = typeof(BaseCharacterComponent);
                Logger.LogInfo($"[ANIM DIAG] BaseCharacterComponent type found: {bccType.FullName}");
                
                var setAnimState = AccessTools.Method(bccType, "SetAnimationState");
                Logger.LogInfo($"[ANIM DIAG] SetAnimationState: {(setAnimState != null ? setAnimState.ToString() : "NOT FOUND")}");
                
                var setGlobalInt = AccessTools.Method(bccType, "SetGlobalState", new[] { typeof(int) });
                Logger.LogInfo($"[ANIM DIAG] SetGlobalState(int): {(setGlobalInt != null ? setGlobalInt.ToString() : "NOT FOUND")}");
                
                var setToolGfx = AccessTools.Method(bccType, "SetToolGraphics", new[] { typeof(int) });
                Logger.LogInfo($"[ANIM DIAG] SetToolGraphics(int): {(setToolGfx != null ? setToolGfx.ToString() : "NOT FOUND")}");
                
                var onStartWalk = AccessTools.Method(bccType, "OnStartWalking");
                Logger.LogInfo($"[ANIM DIAG] OnStartWalking: {(onStartWalk != null ? onStartWalk.ToString() : "NOT FOUND")}");
                
                var onStopped = AccessTools.Method(bccType, "OnStopped");
                Logger.LogInfo($"[ANIM DIAG] OnStopped: {(onStopped != null ? onStopped.ToString() : "NOT FOUND")}");
                
                var tcType = typeof(ToolComponent);
                Logger.LogInfo($"[ANIM DIAG] ToolComponent type found: {tcType.FullName}");
                
                var useTool = AccessTools.Method(tcType, "UseTool");
                Logger.LogInfo($"[ANIM DIAG] UseTool: {(useTool != null ? useTool.ToString() : "NOT FOUND")}");
                
                var useCurrent = AccessTools.Method(tcType, "UseCurrentTool");
                Logger.LogInfo($"[ANIM DIAG] UseCurrentTool: {(useCurrent != null ? useCurrent.ToString() : "NOT FOUND")}");
                
                var animEvent = AccessTools.Method(tcType, "AnimationEventAction");
                Logger.LogInfo($"[ANIM DIAG] AnimationEventAction: {(animEvent != null ? animEvent.ToString() : "NOT FOUND")}");
                
                // Check if patches were actually applied to these methods
                if (setAnimState != null)
                {
                    var patchInfo = Harmony.GetPatchInfo(setAnimState);
                    Logger.LogInfo($"[ANIM DIAG] SetAnimationState patch info: prefixes={patchInfo?.Prefixes?.Count ?? 0}, postfixes={patchInfo?.Postfixes?.Count ?? 0}");
                }
                if (setGlobalInt != null)
                {
                    var patchInfo = Harmony.GetPatchInfo(setGlobalInt);
                    Logger.LogInfo($"[ANIM DIAG] SetGlobalState(int) patch info: prefixes={patchInfo?.Prefixes?.Count ?? 0}, postfixes={patchInfo?.Postfixes?.Count ?? 0}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[ANIM DIAG] Verification failed: {ex}");
            }
            
            // Initialize Local Co-op if enabled
            if (ModConfig.EnableLocalCoop.Value)
            {
                InitializeLocalCoop();
                Logger.LogInfo("Note: Local co-op will only activate when game is started from multiplayer menu");
            }
            // Note: ModConfig.IsHost is a legacy config setting for local co-op mode
            // For online multiplayer, host status is determined by SteamLobbyManager when creating/joining lobbies
            
            // Always initialize Online Co-op Manager for network multiplayer
            InitializeOnlineCoop();
        }
        
        /// <summary>
        /// Initialize local co-op system
        /// </summary>
        private void InitializeLocalCoop()
        {
            Logger.LogInfo("=== LOCAL CO-OP MODE ENABLED ===");
            Logger.LogInfo("Local co-op will activate when you start a game from the multiplayer menu");
            
            // Create LocalCoopManager GameObject
            localCoopManagerObj = new GameObject("LocalCoopManager");
            DontDestroyOnLoad(localCoopManagerObj);
            localCoopManagerObj.AddComponent<LocalCoopManager>();
            
            Logger.LogInfo("LocalCoopManager initialized");
        }
        
        /// <summary>
        /// Initialize online co-op system for network multiplayer
        /// </summary>
        private void InitializeOnlineCoop()
        {
            Logger.LogInfo("=== ONLINE CO-OP MANAGER INITIALIZATION ===");
            
            // Create every persistent online subsystem up front so EnableSync calls cannot silently no-op.
            onlineCoopManagerObj = CreatePersistentComponent<OnlineCoopManager>("OnlineCoopManager").gameObject;

            CreatePersistentComponent<Multiplayer.GameLoadSync>("GameLoadSync");
            CreatePersistentComponent<Multiplayer.DialogueSync>("DialogueSync");
            CreatePersistentComponent<Multiplayer.GameTimeSync>("GameTimeSync");
            CreatePersistentComponent<Multiplayer.WGODestructionSync>("WGODestructionSync");
            CreatePersistentComponent<Multiplayer.WGOStateSync>("WGOStateSync");
            CreatePersistentComponent<Multiplayer.WGORegistry>("WGORegistry");
            CreatePersistentComponent<Multiplayer.WeatherSync>("WeatherSync");
            CreatePersistentComponent<Multiplayer.PlayerParitySync>("PlayerParitySync");
            CreatePersistentComponent<Multiplayer.PlayerVisualSync>("PlayerVisualSync");
            CreatePersistentComponent<Multiplayer.NpcVisualSync>("NpcVisualSync");
            CreatePersistentComponent<Multiplayer.InventorySync>("InventorySync");
            CreatePersistentComponent<Multiplayer.CosmeticsSync>("CosmeticsSync");
            CreatePersistentComponent<Multiplayer.CraftSync>("CraftSync");
            CreatePersistentComponent<Multiplayer.WorkIndicatorSync>("WorkIndicatorSync");
            CreatePersistentComponent<Multiplayer.TechSync>("TechSync");
            CreatePersistentComponent<Multiplayer.QuestSync>("QuestSync");
            CreatePersistentComponent<Multiplayer.ZoneNavSync>("ZoneNavSync");
            CreatePersistentComponent<Multiplayer.CombatSync>("CombatSync");
            CreatePersistentComponent<Multiplayer.PlayerParamSync>("PlayerParamSync");
            CreatePersistentComponent<Multiplayer.SpawnSync>("SpawnSync");
            CreatePersistentComponent<Multiplayer.DungeonSync>("DungeonSync");
            CreatePersistentComponent<Multiplayer.WorkerSync>("WorkerSync");
            CreatePersistentComponent<Multiplayer.FishingSync>("FishingSync");
            CreatePersistentComponent<Multiplayer.DLCSync>("DLCSync");
            CreatePersistentComponent<Multiplayer.LiveWGOTransformSync>("LiveWGOTransformSync");
            CreatePersistentComponent<Multiplayer.HostAuthorityInteractionSync>("HostAuthorityInteractionSync");
            CreatePersistentComponent<Multiplayer.JoinerProfileManager>("JoinerProfileManager");
            CreatePersistentComponent<UI.NetworkDebugOverlay>("NetworkDebugOverlay");
            CreatePersistentComponent<UI.PingIndicator>("PingIndicator");
            CreatePersistentComponent<Utils.FrameProfilerStart>("FrameProfilerStart");
            CreatePersistentComponent<Utils.FrameProfilerEnd>("FrameProfilerEnd");
            
            // Initialize Steam Rich Presence join flow
            Network.SteamJoinFlow.Init();
            Network.SteamJoinFlow.OnJoinRequested += OnSteamJoinRequested;

            // Initialize host-save-list mirror sync (clients see host's save list in lobby)
            Network.HostSaveListSync.Init();
            
            Logger.LogInfo("Online co-op subsystems initialized - they will activate when joining online multiplayer");
        }

        private T CreatePersistentComponent<T>(string objectName) where T : Component
        {
            T existing = FindObjectOfType<T>();
            if (existing != null)
            {
                DontDestroyOnLoad(existing.gameObject);
                return existing;
            }

            var obj = new GameObject(objectName);
            DontDestroyOnLoad(obj);
            return obj.AddComponent<T>();
        }
        
        /// <summary>
        /// Called when someone requests to join via Steam overlay "Join Game" button
        /// </summary>
        private void OnSteamJoinRequested(string connectToken, Steamworks.CSteamID friend)
        {
            Logger.LogInfo($"[CoopMod] Steam join requested: token={connectToken}, friend={friend}");
            
            // Parse the connect token to get host ID
            var hostId = Network.SteamJoinFlow.ParseConnectToken(connectToken);
            if (hostId == Steamworks.CSteamID.Nil)
            {
                Logger.LogWarning("[CoopMod] Invalid connect token, cannot join");
                return;
            }
            
            // Get the friend's name for logging
            string friendName = Steamworks.SteamFriends.GetFriendPersonaName(friend);
            Logger.LogInfo($"[CoopMod] Steam Rich Presence join: Joining {friendName}'s game (host: {hostId})");

            string dlcRequirements = Steamworks.SteamFriends.GetFriendRichPresence(friend, Network.SteamLobbyManager.LobbyDataDLC);
            if (!Network.SteamLobbyManager.TryValidateDLCRequirements(dlcRequirements, out string dlcRejectMessage))
            {
                Logger.LogWarning($"[CoopMod] Steam Rich Presence join blocked by DLC requirements: {dlcRejectMessage}");
                if (GUIElements.me != null && GUIElements.me.dialog != null)
                {
                    GUIElements.me.dialog.OpenOK(dlcRejectMessage);
                }
                return;
            }
            
            // Directly request lobby info from host and join
            // No need to open menu or show invitation - just connect!
            Network.SteamP2PManager.Instance?.RequestLobbyFromHost(hostId);
        }

        private void Update()
        {
            var __profSw = System.Diagnostics.Stopwatch.StartNew();
            try { UpdateInternal(); }
            finally { GraveyardKeeperCoop.Utils.FrameProfiler.Record("CoopMod.Update", __profSw.ElapsedTicks); }
        }

        private void UpdateInternal()
        {
            // Process incoming P2P messages every frame
            // This allows us to receive invite notifications in real-time
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.Update();
            }

            // Process incoming chat sync messages
            Multiplayer.LobbyChatSync.ProcessIncomingMessages();

            // Retry joiner save requests if the initial request packet was lost.
            Multiplayer.SaveTransferManager.UpdateRetry();
        }
    }
    
}

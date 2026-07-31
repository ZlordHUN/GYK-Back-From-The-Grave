using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Steamworks;
using GraveyardKeeperCoop.Multiplayer;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Synchronizes FlowScript cutscenes between online co-op players.
    ///
    /// Problem: When one player's actions trigger a FlowScript (e.g., digging up Gerry's grave
    /// triggers his cutscene), only that player sees it. The remote player sees nothing.
    ///
    /// Solution: Intercept FlowScript creation plus events dispatched into already
    /// running global/WGO graphs, then promote the pending source when vanilla
    /// actually shows its cinematic letterbox. Craft completion and WGO zero-HP
    /// scripts can still be announced immediately because those triggers are
    /// authoritative. Nearby peers observe the initiator; only known global
    /// cutscenes run a second local presentation.
    ///
    /// Context flags ensure we only sync FlowScripts triggered by:
    /// - CraftComponent.ProcessFinishedCraft → end_script (e.g., grave digging → Gerry)
    /// - WorldGameObject.DoZeroHPActivity → script_after_hp_0 (e.g., destroying an object)
    ///
    /// Story graphs are never replayed merely to reproduce presentation because doing
    /// so would execute their quest, item, and world mutations twice.
    /// </summary>
    [HarmonyPatch]
    public static class CutsceneSyncPatches
    {
        /// <summary>
        /// Re-entry guard: set when we are executing a FlowScript received from the remote player.
        /// Prevents the prefix from broadcasting it back (infinite loop).
        /// </summary>
        private static bool isProcessingRemote = false;

        /// <summary>
        /// Context flag: true only while inside ProcessFinishedCraft or DoZeroHPActivity.
        /// Only FlowScripts triggered within these contexts are synced.
        /// </summary>
        private static bool isInSyncableContext = false;

        private const float CUTSCENE_ACTIVATION_DISTANCE = 350f;
        private const float DEFERRED_CUTSCENE_TIMEOUT = 90f;
        private const float COMPLETED_DEFERRED_CUTSCENE_TTL = 180f;
        private const float LOCAL_SYNCED_CUTSCENE_TTL = 180f;
        private const float CUTSCENE_ORIGIN_MATCH_TOLERANCE = 24f;
        private const float DEFERRED_FAST_FORWARD_STEP_TIMEOUT = 2f;
        private const float DEFERRED_FAST_FORWARD_STEP_DELAY = 0.08f;
        // Most flow nodes execute synchronously. A modest attribution window covers
        // short movement/fade preambles; an active dialogue keeps its own candidate
        // alive because the player may leave a bubble open indefinitely.
        private const float CINEMATIC_CANDIDATE_TTL = 15f;
        private const float LOCAL_ACTOR_COMPLETION_SETTLE_SECONDS = 0.25f;
        private const float LOCAL_ACTOR_COMPLETION_MAX_WAIT_SECONDS = 12f;
        private const float FIRST_BURIAL_CUTSCENE_HANDOFF_SECONDS = 2f;
        private const string RED_EYE_INTRO_SCRIPT = "red_eye_talk_1";
        private const string GERRY_DIG_UP_SCRIPT = "skull_spawn_after_dig";
        private const string FIRST_BURIAL_FOLLOWUP_QUEST =
            "ghost_come_after_1st_burial";
        private const string GERRY_WGO_ID = "talking_skull";
        private const string GERRY_NPC_ID = "crafting_skull_3";
        private const string GERRY_DIG_QUEST_ID = "dig_graved_skull";
        private const string GERRY_PLAYER_TASK_ID = "player_gerry";
        private const string GERRY_FINAL_POINT_TAG = "skull_talk_before_donkey_3";
        private const string DONKEY_WGO_ID = "donkey";
        private const string DONKEY_CUSTOM_TAG = "donkey";
        private const string DONKEY_CEMETERY_POINT_TAG = "donkey_cemetery_point";
        private const string DONKEY_CEMETERY_ARRIVAL_EVENT = "on_came_to_cemetery";
        private const string DONKEY_FIRST_QUEST_ID = "go_to_talk_with_donkey_first_time";
        private const float GERRY_DIG_UP_PLAYER_WALK_SPEED = 1.4f;
        private const float GERRY_DIG_UP_CUTSCENE_SPEED_TIMEOUT = 90f;
        private static readonly Vector3 GERRY_DIG_UP_FINAL_WORLD_POS = new Vector3(3996f, -1374f, 0f);

        private static PendingRemoteCutscene pendingRemoteCutscene;
        private static RemoteCutsceneSession remoteCutsceneSession;
        private static RemoteNpcVisualTail remoteNpcVisualTail;
        private static readonly List<CompletedDeferredCutscene> completedDeferredCutscenes = new List<CompletedDeferredCutscene>();
        private static readonly List<LocalSyncedCutscene> localSyncedCutscenes = new List<LocalSyncedCutscene>();
        private static readonly Queue<DeferredDialogueOp> deferredCatchUpDialogueOps = new Queue<DeferredDialogueOp>();
        private static PotentialCinematic recentPotentialCinematic;
        private static bool isGerryDigUpCutsceneActive;
        private static float gerryDigUpCutsceneStartedAt;
        private static bool hasGerryDigUpPreCutscenePlayerSpeed;
        private static float gerryDigUpPreCutscenePlayerSpeed = LazyConsts.PLAYER_SPEED;
        private static CSteamID deferredCatchUpSenderID = CSteamID.Nil;
        private static string deferredCatchUpScriptName = "";
        private static int suppressedDeferredCatchUpDialogueCaptures;
        private static bool isApplyingRemoteCamera;
        private static bool remoteCameraApplied;
        private static GameObject remoteCameraFallbackTarget;
        private static float suppressDisabledInteractionUntil;
        private static int cinematicPresentationCallDepth;

        /// <summary>
        /// True only while this machine is running another player's FlowScript for
        /// presentation. Personal side effects from that mirror must not mutate the
        /// observing player's character state.
        /// </summary>
        internal static bool IsApplyingRemotePresentation =>
            isProcessingRemote ||
            remoteCutsceneSession?.MirrorsLocalExecution == true;

        private sealed class PendingRemoteCutscene
        {
            public CSteamID SenderID;
            public string ScriptName;
            public Vector3 OriginWorldPos;
            public byte Facing;
            public string OriginZoneId;
            public float ReceivedAt;
            public bool RemoteCompleted;
            public bool ProgressionApplied;
            public bool FinalWorldStateApplied;
            public readonly List<DeferredDialogueOp> BufferedDialogueOps = new List<DeferredDialogueOp>();
        }

        /// <summary>
        /// A live cutscene currently being performed by the remote player. This is not
        /// a deferred replay: it only records that the remote performance is still active,
        /// so the local player can attach to it at the remote player's current position.
        /// </summary>
        private sealed class RemoteCutsceneSession
        {
            public CSteamID SenderID;
            public string ScriptName;
            public Vector3 OriginWorldPos;
            public byte Facing;
            public string OriginZoneId;
            public float StartedAt;
            public bool IsParticipating;
            public bool JoinWalkStarted;
            public bool MirrorsLocalExecution;
            public bool ScopeRejectionLogged;
            public CutsceneCameraState CameraState;
        }

        private sealed class RemoteNpcVisualTail
        {
            public CSteamID SenderID;
            public float StartedAt;
        }

        private sealed class CompletedDeferredCutscene
        {
            public CSteamID SenderID;
            public string ScriptName;
            public Vector3 OriginWorldPos;
            public string OriginZoneId;
            public float CompletedAt;
        }

        private sealed class LocalSyncedCutscene
        {
            public string ScriptName;
            public Vector3 OriginWorldPos;
            public string OriginZoneId;
            public float StartedAt;
            public bool CompletionSent;
            public bool MirrorsRemoteExecution;
            public bool SuppressCompletionBroadcast;
            public float CompletionRequestedAt;
            public float CompletionSettledAt;
            public string CompletionReason;
            public bool VisualTailActive;
        }

        private sealed class PotentialCinematic
        {
            public string ScriptName;
            public float StartedAt;
            public WorldGameObject OriginActor;
            public CutsceneCameraState PendingCameraState;
        }

        /// <summary>
        /// Subscribe to CutsceneSync network events. Called when online coop starts.
        /// </summary>
        public static void Enable()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnCutsceneSyncReceived -= OnCutsceneSyncReceived;
                SteamP2PManager.Instance.OnCutsceneSyncReceived += OnCutsceneSyncReceived;
                SteamP2PManager.Instance.OnCutsceneCompleteReceived -= OnCutsceneCompleteReceived;
                SteamP2PManager.Instance.OnCutsceneCompleteReceived += OnCutsceneCompleteReceived;
                SteamP2PManager.Instance.OnCutsceneCameraReceived -= OnCutsceneCameraReceived;
                SteamP2PManager.Instance.OnCutsceneCameraReceived += OnCutsceneCameraReceived;
            }
            CoopMod.Logger.LogInfo("[CutsceneSync] Enabled");
        }

        /// <summary>
        /// Unsubscribe from network events. Called when online coop ends.
        /// </summary>
        public static void Disable()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnCutsceneSyncReceived -= OnCutsceneSyncReceived;
                SteamP2PManager.Instance.OnCutsceneCompleteReceived -= OnCutsceneCompleteReceived;
                SteamP2PManager.Instance.OnCutsceneCameraReceived -= OnCutsceneCameraReceived;
            }
            RestoreRemoteCutsceneCamera("cutscene sync disabled");
            isInSyncableContext = false;
            isProcessingRemote = false;
            pendingRemoteCutscene = null;
            remoteCutsceneSession = null;
            remoteNpcVisualTail = null;
            completedDeferredCutscenes.Clear();
            localSyncedCutscenes.Clear();
            recentPotentialCinematic = null;
            cinematicPresentationCallDepth = 0;
            suppressDisabledInteractionUntil = 0f;
            OnlineCoopManager.Instance?.EndObservedCutsceneFollow("cutscene sync disabled");
            EndDeferredCutsceneCatchUp("sync disabled");
            EndGerryDigUpCutsceneIfActive("sync disabled");
            CoopMod.Logger.LogInfo("[CutsceneSync] Disabled");
        }

        #region Context Tracking — CraftComponent.ProcessFinishedCraft

        /// <summary>
        /// Set sync context before craft's end_script is processed.
        /// The 3 end_script branches inside ProcessFinishedCraft are:
        ///   "g:name"      → GS.RunFlowScript(name)                    [global]
        ///   "name:event"  → GS.RunFlowScript(name) + FireEvent(event) [global + event]
        ///   "plain_name"  → wgo.AttachFlowScript(name)                [WGO-attached]
        /// Our GS.RunFlowScript and AttachFlowScript prefixes will fire within this context.
        /// </summary>
        [HarmonyPatch(typeof(CraftComponent), "ProcessFinishedCraft")]
        [HarmonyPrefix]
        public static void CraftFinished_Prefix() => isInSyncableContext = true;

        [HarmonyPatch(typeof(CraftComponent), "ProcessFinishedCraft")]
        [HarmonyPostfix]
        public static void CraftFinished_Postfix() => isInSyncableContext = false;

        #endregion

        #region Context Tracking — WorldGameObject.DoZeroHPActivity

        /// <summary>
        /// Set sync context before WGO zero-HP script runs.
        /// DoZeroHPActivity processes script_after_hp_0 with:
        ///   "g:name"      → GS.RunFlowScript(name)       [global]
        ///   "plain_name"  → wgo.AttachFlowScript(name)    [WGO-attached]
        /// </summary>
        [HarmonyPatch(typeof(WorldGameObject), "DoZeroHPActivity")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static void ZeroHP_Prefix() => isInSyncableContext = true;

        [HarmonyPatch(typeof(WorldGameObject), "DoZeroHPActivity")]
        [HarmonyPostfix]
        public static void ZeroHP_Postfix() => isInSyncableContext = false;

        #endregion

        /// <summary>
        /// Broadcast a FlowScript trigger AND a walk-to-me request so the remote player visibly walks
        /// over to the local digger instead of being teleported / left out of frame.
        /// </summary>
        private static LocalSyncedCutscene BroadcastSyncAndWalk(
            string scriptName,
            WorldGameObject seedActor = null)
        {
            var p2p = SteamP2PManager.Instance;
            if (p2p == null) return null;

            NpcVisualSync.Instance?.BeginLocalCutsceneActorSession(
                seedActor,
                $"FlowScript {scriptName}");

            Vector3 cutscenePos = Vector3.zero;
            byte facing = 0;
            string zoneId = "";
            bool hasOrigin = false;

            // Pull local player's position + facing so the remote can decide whether to join now
            // or defer until they enter the same area. The same values drive CutsceneWalkTo.
            try
            {
                var localPlayer = MainGame.me?.player;
                var character = MainGame.me?.player_char;
                if (localPlayer != null && character != null)
                {
                    cutscenePos = localPlayer.transform.position;
                    facing = (byte)character.anim_direction;
                    zoneId = GetLocalPlayerZoneId();
                    hasOrigin = true;
                }
                else
                {
                    CoopMod.Logger.LogWarning("[CutsceneSync] Could not capture local player pos/facing for scoped CutsceneSync");
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[CutsceneSync] Failed to capture scope for script '{scriptName}': {ex.Message}");
            }

            p2p.SendCutsceneSync(scriptName, cutscenePos, facing, zoneId);

            if (hasOrigin)
            {
                p2p.SendCutsceneWalkTo(cutscenePos, facing);
            }

            PruneLocalSyncedCutscenes();
            var localCutscene = new LocalSyncedCutscene
            {
                ScriptName = scriptName,
                OriginWorldPos = cutscenePos,
                OriginZoneId = zoneId ?? "",
                StartedAt = Time.realtimeSinceStartup
            };
            localSyncedCutscenes.Add(localCutscene);
            return localCutscene;
        }

        #region FlowScript Intercept — GS.RunFlowScript

        /// <summary>
        /// Intercept global FlowScript execution.
        /// When GS.RunFlowScript is called inside a syncable context (craft completion or
        /// WGO zero HP), broadcast the script name to the remote player.
        ///
        /// This catches:
        /// - "g:name" end_scripts (after the "g:" prefix is stripped by the caller)
        /// - "name:event" end_scripts (the RunFlowScript(name) call; event is fired separately)
        /// - "g:name" script_after_hp_0 entries
        /// </summary>
        [HarmonyPatch(typeof(GS), nameof(GS.RunFlowScript))]
        [HarmonyPrefix]
        public static bool RunFlowScript_Prefix(
            string uscript_name,
            ref CustomFlowScript.OnFinishedDelegate on_finished,
            ref CustomFlowScript __result)
        {
            if (HostAuthorityInteractionSync.IsApplyingZeroHpMutation)
            {
                __result = null;
                CoopMod.Logger.LogInfo(
                    $"[CutsceneSync] Suppressed host-side zero-HP presentation FlowScript \"{uscript_name}\"; " +
                    "the triggering player owns its presentation");
                return false;
            }

            if (isProcessingRemote) return true;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled) return true;

            if (string.IsNullOrEmpty(uscript_name)) return true;

            // A player can enter the same zone shortly after the initiator. Once a
            // live session exists, do not start a second local copy of that script;
            // the player joins the initiator at the current cutscene position.
            if (remoteCutsceneSession != null &&
                string.Equals(
                    remoteCutsceneSession.ScriptName,
                    uscript_name,
                    System.StringComparison.OrdinalIgnoreCase))
            {
                __result = null;
                CoopMod.Logger.LogInfo(
                    $"[CutsceneSync] Suppressed duplicate local FlowScript \"{uscript_name}\" while observing its remote live session");
                TryJoinRemoteCutsceneSession("matching local trigger", true);
                return false;
            }

            if (!isInSyncableContext)
            {
                // Most story cutscenes begin from zone/global scripts rather than a
                // craft callback. Record the script now and promote it only if it
                // actually disables the player with cinematic bars. This detects
                // cutscenes generically without broadcasting every utility script.
                RecordPotentialCinematic(uscript_name);
                return true;
            }

            recentPotentialCinematic = null;
            CoopMod.Logger.LogInfo($"[CutsceneSync] Local GS.RunFlowScript(\"{uscript_name}\") in sync context — broadcasting to remote");
            BeginGerryDigUpCutsceneIfNeeded(uscript_name, "local GS.RunFlowScript");
            var localCutscene = BroadcastSyncAndWalk(uscript_name);
            WrapCutsceneFinishedCallback(localCutscene, ref on_finished);
            return true;
        }

        #endregion

        #region FlowScript Intercept — WorldGameObject.AttachFlowScript

        /// <summary>
        /// Intercept WGO-attached FlowScript execution.
        /// When AttachFlowScript is called inside a syncable context, broadcast the script name.
        /// The triggering peer retains script ownership while nearby peers attach to the live
        /// presentation and receive its synchronized dialogue and actor state.
        ///
        /// Skips SmartExpressions (names starting with ':') — those are not FlowScripts.
        /// </summary>
        [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.AttachFlowScript))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static bool AttachFlowScript_Prefix(
            WorldGameObject __instance,
            string flowscript_name,
            ref CustomFlowScript.OnFinishedDelegate on_finished,
            ref CustomFlowScript __result)
        {
            if (HostAuthorityInteractionSync.IsApplyingZeroHpMutation)
            {
                __result = null;
                CoopMod.Logger.LogInfo(
                    $"[CutsceneSync] Suppressed host-side zero-HP presentation FlowScript \"{flowscript_name}\"; " +
                    "the triggering player owns its presentation");
                return false;
            }

            if (isProcessingRemote) return true;
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled) return true;

            if (string.IsNullOrEmpty(flowscript_name)) return true;

            // Skip SmartExpressions (not FlowScripts)
            if (flowscript_name[0] == ':') return true;

            if (!isInSyncableContext)
            {
                RecordPotentialCinematic(flowscript_name, __instance);
                return true;
            }

            recentPotentialCinematic = null;
            CoopMod.Logger.LogInfo($"[CutsceneSync] Local WGO.AttachFlowScript(\"{flowscript_name}\") in sync context — broadcasting to remote");
            BeginGerryDigUpCutsceneIfNeeded(flowscript_name, "local AttachFlowScript");
            var localCutscene = BroadcastSyncAndWalk(
                flowscript_name,
                __instance);
            WrapCutsceneFinishedCallback(localCutscene, ref on_finished);
            return true;
        }

        #endregion

        #region Existing FlowScript Event Intercepts

        /// <summary>
        /// WGO interaction events run on an already-attached FlowScript and therefore
        /// never pass through RunFlowScript or AttachFlowScript. Record their owning
        /// script and event so the actual letterbox transition can promote them.
        /// </summary>
        [HarmonyPatch(
            typeof(WorldGameObject),
            nameof(WorldGameObject.FireEvent),
            new System.Type[] { typeof(string), typeof(float) })]
        [HarmonyPrefix]
        public static void WorldGameObjectFireEvent_Prefix(
            WorldGameObject __instance,
            string event_id)
        {
            RecordWgoEventPotential(__instance, event_id);
        }

        [HarmonyPatch(
            typeof(WorldGameObject),
            nameof(WorldGameObject.FireEvent),
            new System.Type[]
            {
                typeof(string),
                typeof(float),
                typeof(string)
            })]
        [HarmonyPrefix]
        public static void WorldGameObjectFireEventWithParam_Prefix(
            WorldGameObject __instance,
            string event_id)
        {
            RecordWgoEventPotential(__instance, event_id);
        }

        /// <summary>
        /// Global scripts can stay alive and receive a later named event. That event
        /// is the real cutscene entry point, not the earlier script creation.
        /// </summary>
        [HarmonyPatch(
            typeof(CustomFlowScript),
            nameof(CustomFlowScript.FireEvent),
            new System.Type[] { typeof(string) })]
        [HarmonyPrefix]
        public static void CustomFlowScriptFireEvent_Prefix(
            CustomFlowScript __instance,
            string event_id)
        {
            RecordCustomFlowEventPotential(__instance, event_id);
        }

        [HarmonyPatch(
            typeof(CustomFlowScript),
            nameof(CustomFlowScript.FireEvent),
            new System.Type[] { typeof(string), typeof(string) })]
        [HarmonyPrefix]
        public static void CustomFlowScriptFireEventWithParam_Prefix(
            CustomFlowScript __instance,
            string event_id)
        {
            RecordCustomFlowEventPotential(__instance, event_id);
        }

        [HarmonyPatch(
            typeof(FlowScriptEngine),
            nameof(FlowScriptEngine.SendEvent))]
        [HarmonyPrefix]
        public static void GlobalFlowEvent_Prefix(string event_name)
        {
            if (!string.IsNullOrEmpty(event_name))
            {
                RecordPotentialCinematic(
                    $"global-event:{event_name}");
            }
        }

        private static void RecordWgoEventPotential(
            WorldGameObject wgo,
            string eventId)
        {
            if (wgo == null || string.IsNullOrEmpty(eventId))
                return;

            string scriptName = wgo.obj_def?.attached_script;
            if (string.IsNullOrEmpty(scriptName))
                scriptName = wgo.obj_id;
            if (string.IsNullOrEmpty(scriptName))
                scriptName = wgo.custom_tag;
            if (string.IsNullOrEmpty(scriptName))
                return;

            RecordPotentialCinematic(
                $"{scriptName}:{eventId}",
                wgo);
        }

        private static void RecordCustomFlowEventPotential(
            CustomFlowScript flowScript,
            string eventId)
        {
            if (flowScript == null ||
                string.IsNullOrEmpty(flowScript.script_name) ||
                string.IsNullOrEmpty(eventId))
            {
                return;
            }

            WorldGameObject origin =
                flowScript.GetComponentInParent<WorldGameObject>();
            RecordPotentialCinematic(
                $"{flowScript.script_name}:{eventId}",
                origin);
        }

        private static void RecordPotentialCinematic(
            string scriptName,
            WorldGameObject originActor = null)
        {
            if (isProcessingRemote ||
                HostAuthorityInteractionSync.IsApplyingZeroHpMutation ||
                SyncBehaviour.GlobalRemoteApplyActive ||
                string.IsNullOrEmpty(scriptName))
            {
                return;
            }

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return;

            recentPotentialCinematic = new PotentialCinematic
            {
                ScriptName = scriptName,
                StartedAt = Time.realtimeSinceStartup,
                OriginActor = originActor
            };
        }

        #endregion

        #region Cutscene Completion Tracking

        // Ownership: CutsceneSyncPatches owns network cutscene propagation/completion.
        // This observer runs after the game restores player control and before generic
        // local cinematic cleanup, so synced cutscenes are closed under the network owner.
        [HarmonyPatch(typeof(GS), nameof(GS.SetPlayerEnable))]
        [HarmonyPrefix]
        public static void SetPlayerEnable_Prefix()
        {
            cinematicPresentationCallDepth++;
        }

        [HarmonyPatch(typeof(GS), nameof(GS.SetPlayerEnable))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Normal)]
        public static void SetPlayerEnable_Postfix(bool player_enabled, bool affect_cinematic)
        {
            if (cinematicPresentationCallDepth > 0)
                cinematicPresentationCallDepth--;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled ||
                !player_enabled)
                return;

            recentPotentialCinematic = null;
            CompleteLocalSyncedCutscenes("player control restored");
            EndGerryDigUpCutsceneIfActive("player control restored");
        }

        [HarmonyPatch(typeof(GS), nameof(GS.AffectCinematic))]
        [HarmonyPrefix]
        public static void AffectCinematic_Prefix()
        {
            cinematicPresentationCallDepth++;
        }

        [HarmonyPatch(typeof(GS), nameof(GS.AffectCinematic))]
        [HarmonyPostfix]
        public static void AffectCinematic_Postfix(bool show)
        {
            if (cinematicPresentationCallDepth > 0)
                cinematicPresentationCallDepth--;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled ||
                show)
            {
                return;
            }

            recentPotentialCinematic = null;
            CompleteLocalSyncedCutscenes("cinematic presentation restored");
        }

        /// <summary>
        /// All vanilla letterbox paths converge here: SetPlayerEnable,
        /// AffectCinematic, and the direct Camera Letterbox flow node.
        /// </summary>
        [HarmonyPatch(
            typeof(CameraTools),
            nameof(CameraTools.TweenLetterbox))]
        [HarmonyPrefix]
        public static void TweenLetterbox_Prefix(bool show)
        {
            if (show)
                PromotePotentialCinematicIfNeeded();
        }

        [HarmonyPatch(
            typeof(CameraTools),
            nameof(CameraTools.TweenLetterbox))]
        [HarmonyPostfix]
        public static void TweenLetterbox_Postfix(bool show)
        {
            if (show || cinematicPresentationCallDepth > 0)
                return;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return;

            recentPotentialCinematic = null;
            CompleteLocalSyncedCutscenes("direct letterbox restored");
        }

        private static void PromotePotentialCinematicIfNeeded()
        {
            PotentialCinematic candidate = recentPotentialCinematic;
            recentPotentialCinematic = null;
            if (candidate == null ||
                remoteCutsceneSession != null ||
                NpcInteractionSyncPatches.HasRemoteNpcVisualAuthority())
            {
                return;
            }

            bool interactionStillActive =
                NpcInteractionSyncPatches.HasLocalNpcVisualAuthority() ||
                DialogueSync.Instance?.IsInSyncedDialogue == true;
            if (!interactionStillActive &&
                Time.realtimeSinceStartup - candidate.StartedAt >
                    CINEMATIC_CANDIDATE_TTL)
            {
                return;
            }

            PruneLocalSyncedCutscenes();
            for (int i = 0; i < localSyncedCutscenes.Count; i++)
            {
                if (!localSyncedCutscenes[i].CompletionSent)
                    return;
            }

            CoopMod.Logger.LogInfo(
                $"[CutsceneSync] Flow/event \"{candidate.ScriptName}\" entered cinematic mode — broadcasting generic live session");
            // Player-bound cutscenes belong to the player who triggered them. Only
            // reciprocal global mirrors (the Red Eye intro) need the deterministic
            // lobby-host owner to prevent both peers claiming the first player line.
            EnsureSharedCutsceneDialogueOwner(
                RequiresLocalFlowScriptMirror(candidate.ScriptName)
                    ? CSteamID.Nil
                    : SteamUser.GetSteamID());
            BroadcastSyncAndWalk(
                candidate.ScriptName,
                candidate.OriginActor);
            if (candidate.PendingCameraState != null)
            {
                SteamP2PManager.Instance?.SendCutsceneCamera(
                    candidate.PendingCameraState);
            }
        }

        private static void EnsureSharedCutsceneDialogueOwner(
            CSteamID preferredOwner = default(CSteamID))
        {
            CSteamID owner = preferredOwner;
            if (owner == CSteamID.Nil)
                owner = SteamLobbyManager.Instance?.GetLobbyOwner() ?? CSteamID.Nil;
            if (owner == CSteamID.Nil)
                owner = SteamUser.GetSteamID();

            DialogueSync.Instance?.EnsureCutsceneDialogueOwner(owner);
        }

        internal static bool ShouldSuppressAmbientSpeechRelay()
        {
            // The new-game intro and reciprocal global cutscenes execute the same
            // FlowScript locally on both machines. Relaying their WorldGameObject.Say
            // calls creates a second bubble whose click advances the script twice.
            return GameLoadSync.Instance?.IsInIntroPhase == true ||
                   remoteCutsceneSession?.MirrorsLocalExecution == true ||
                   HasActiveMirroredLocalCutscene();
        }

        internal static void MarkRemoteFirstBurialCutsceneHandoff(
            string questId)
        {
            if (!string.Equals(
                    questId,
                    FIRST_BURIAL_FOLLOWUP_QUEST,
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            suppressDisabledInteractionUntil =
                Time.realtimeSinceStartup +
                FIRST_BURIAL_CUTSCENE_HANDOFF_SECONDS;
        }

        internal static bool ShouldSuppressTransientDisabledInteraction()
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return false;

            PruneLocalSyncedCutscenes();
            return Time.realtimeSinceStartup <=
                       suppressDisabledInteractionUntil ||
                   HasActiveLocalCutscenePresentation() ||
                   remoteCutsceneSession != null ||
                   pendingRemoteCutscene != null;
        }

        #endregion

        #region Gerry Dig-Up Movement Pacing

        [HarmonyPatch(typeof(MovementComponent), nameof(MovementComponent.GoTo), new System.Type[]
        {
            typeof(Vector2),
            typeof(bool),
            typeof(GJCommons.VoidDelegate),
            typeof(GJCommons.VoidDelegate),
            typeof(bool),
            typeof(MovementComponent.GoToMethod),
            typeof(string),
            typeof(System.Nullable<uint>),
            typeof(bool),
            typeof(GDPoint)
        })]
        [HarmonyPostfix]
        public static void MovementGoTo_Postfix(MovementComponent __instance, bool from_script)
        {
            if (!from_script || !IsGerryDigUpCutsceneActive())
            {
                return;
            }

            var player = MainGame.me?.player;
            if (__instance?.wgo == null || player == null || __instance.wgo != player)
            {
                return;
            }

            ClampGerryDigUpPlayerMovementSpeed(__instance, "GoTo");
        }

        [HarmonyPatch(typeof(MovementComponent), nameof(MovementComponent.SetSpeed))]
        [HarmonyPrefix]
        public static void MovementSetSpeed_Prefix(MovementComponent __instance, ref float speed)
        {
            if (!IsGerryDigUpCutsceneActive())
            {
                return;
            }

            var player = MainGame.me?.player;
            if (__instance?.wgo == null || player == null || __instance.wgo != player)
            {
                return;
            }

            if (speed <= 0f || Mathf.Abs(speed - GERRY_DIG_UP_PLAYER_WALK_SPEED) <= 0.01f)
            {
                return;
            }

            CoopMod.Logger.LogInfo($"[CutsceneSync] Gerry scripted player SetSpeed clamped: {speed:F2} -> {GERRY_DIG_UP_PLAYER_WALK_SPEED:F2}");
            speed = GERRY_DIG_UP_PLAYER_WALK_SPEED;
        }

        [HarmonyPatch(typeof(MovementComponent), "UpdateMovement", new System.Type[] { typeof(Vector2), typeof(float) })]
        [HarmonyPrefix]
        public static void MovementUpdateMovement_Prefix(MovementComponent __instance)
        {
            if (!IsGerryDigUpCutsceneActive())
            {
                return;
            }

            ClampGerryDigUpPlayerMovementSpeed(__instance, "UpdateMovement", false);
        }

        [HarmonyPatch(typeof(SpeechBubbleGUI), nameof(SpeechBubbleGUI.ShowMessage), new System.Type[]
        {
            typeof(long),
            typeof(string),
            typeof(Transform),
            typeof(GJCommons.VoidDelegate),
            typeof(bool),
            typeof(bool),
            typeof(SpeechBubbleGUI.SpeechBubbleType),
            typeof(bool),
            typeof(SmartSpeechEngine.VoiceID)
        })]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool SpeechBubbleGUI_ShowMessage_CatchUpPrefix(string txt, GJCommons.VoidDelegate on_disappeared)
        {
            if (!TryPeekDeferredCatchUpOp(DeferredDialogueOpType.Advance, out _))
            {
                return true;
            }

            string scriptName = deferredCatchUpScriptName;
            DequeueDeferredCatchUpOp();
            suppressedDeferredCatchUpDialogueCaptures++;
            CoopMod.Logger.LogInfo($"[CutsceneSync] Suppressed missed deferred dialogue \"{txt}\" while catching up \"{scriptName}\"");

            try
            {
                on_disappeared?.Invoke();
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[CutsceneSync] Error advancing suppressed deferred dialogue \"{txt}\": {ex.Message}");
            }

            return false;
        }

        [HarmonyPatch(typeof(MultiAnswerGUI), nameof(MultiAnswerGUI.ShowAnswers), new System.Type[]
        {
            typeof(List<AnswerVisualData>),
            typeof(Transform),
            typeof(MultiAnswerGUI.MultiAnswerResult),
            typeof(bool),
            typeof(GJCommons.VoidDelegate),
            typeof(WorldGameObject)
        })]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool MultiAnswerGUI_ShowAnswers_CatchUpPrefix(List<AnswerVisualData> answers, MultiAnswerGUI.MultiAnswerResult on_chosen, GJCommons.VoidDelegate on_disappeared)
        {
            if (!TryPeekDeferredCatchUpOp(DeferredDialogueOpType.Choice, out DeferredDialogueOp op))
            {
                return true;
            }

            string answerId = ResolveCatchUpChoiceAnswer(answers, op);
            if (string.IsNullOrEmpty(answerId))
            {
                CoopMod.Logger.LogWarning($"[CutsceneSync] Could not resolve deferred catch-up choice {op.ChoiceIndex} for \"{deferredCatchUpScriptName}\"");
                return true;
            }

            string scriptName = deferredCatchUpScriptName;
            DequeueDeferredCatchUpOp();
            CoopMod.Logger.LogInfo($"[CutsceneSync] Applied missed deferred choice {op.ChoiceIndex} ({answerId}) while catching up \"{scriptName}\"");

            try
            {
                on_chosen?.Invoke(answerId);
                on_disappeared?.Invoke();
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[CutsceneSync] Error applying deferred catch-up choice {op.ChoiceIndex}: {ex.Message}");
            }

            return false;
        }

        #endregion

        #region Cutscene Camera Sync

        [HarmonyPatch(typeof(CameraTools), nameof(CameraTools.CameraFlyTo))]
        [HarmonyPrefix]
        public static void CameraFlyTo_Prefix(Transform target, float duration)
        {
            if (target == null || isApplyingRemoteCamera)
                return;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return;

            WorldGameObject targetWgo =
                target.GetComponentInParent<WorldGameObject>();
            var state = new CutsceneCameraState
            {
                FlyBack = false,
                TargetUniqueId = targetWgo?.unique_id ?? 0L,
                TargetCustomTag = targetWgo?.custom_tag ?? "",
                TargetObjId = targetWgo?.obj_id ?? "",
                TargetName = target.name ?? "",
                TargetRelativePath = GetRelativeTransformPath(
                    targetWgo?.transform,
                    target),
                TargetPosition = target.position,
                Duration = duration
            };
            if (ShouldBroadcastLocalCameraCommand())
            {
                SteamP2PManager.Instance?.SendCutsceneCamera(state);
            }
            else if (recentPotentialCinematic != null &&
                     remoteCutsceneSession == null)
            {
                // zombie_in_mortuary is the one vanilla graph that moves the
                // camera before enabling its letterbox. Retain that first target
                // until the letterbox promotes the pending live session.
                recentPotentialCinematic.PendingCameraState = state;
            }
        }

        [HarmonyPatch(typeof(CameraTools), nameof(CameraTools.CameraFlyBack))]
        [HarmonyPrefix]
        public static void CameraFlyBack_Prefix(float duration)
        {
            if (!ShouldBroadcastLocalCameraCommand())
            {
                if (recentPotentialCinematic != null)
                    recentPotentialCinematic.PendingCameraState = null;
                return;
            }

            SteamP2PManager.Instance?.SendCutsceneCamera(
                new CutsceneCameraState
                {
                    FlyBack = true,
                    Duration = duration
                });
        }

        private static bool ShouldBroadcastLocalCameraCommand()
        {
            if (isApplyingRemoteCamera ||
                remoteCutsceneSession?.IsParticipating == true)
            {
                return false;
            }

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return false;

            PruneLocalSyncedCutscenes();
            return HasActiveLocalCutscenePresentation();
        }

        private static void OnCutsceneCameraReceived(
            CSteamID senderID,
            CutsceneCameraState state)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (state == null ||
                onlineCoop == null ||
                !onlineCoop.IsOnlineCoopEnabled ||
                !onlineCoop.IsRemotePlayer(senderID))
            {
                return;
            }

            RemoteCutsceneSession session = remoteCutsceneSession;
            if (session == null || session.SenderID != senderID)
            {
                CoopMod.Logger.LogInfo(
                    "[CutsceneSync] Ignoring camera command without a matching " +
                    "live remote cutscene");
                return;
            }

            session.CameraState = state;
            if (session.IsParticipating &&
                !session.MirrorsLocalExecution)
            {
                ApplyRemoteCutsceneCamera(state);
            }
        }

        private static void ApplyRemoteCutsceneCamera(
            CutsceneCameraState state)
        {
            if (state == null)
                return;

            isApplyingRemoteCamera = true;
            try
            {
                if (state.FlyBack)
                {
                    if (remoteCameraApplied)
                    {
                        CameraTools.CameraFlyBack(
                            null,
                            Mathf.Max(0f, state.Duration));
                    }
                    remoteCameraApplied = false;
                    return;
                }

                Transform target = ResolveRemoteCameraTarget(state);
                if (target == null)
                {
                    CoopMod.Logger.LogWarning(
                        "[CutsceneSync] Could not resolve remote camera target");
                    return;
                }

                CameraTools.CameraFlyTo(
                    target,
                    null,
                    Mathf.Max(0f, state.Duration));
                remoteCameraApplied = true;
                CoopMod.Logger.LogInfo(
                    $"[CutsceneSync] Applied remote camera target " +
                    $"'{state.TargetObjId}'/'{state.TargetName}' " +
                    $"(uid={state.TargetUniqueId})");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[CutsceneSync] Failed to apply remote camera command: " +
                    $"{ex.Message}");
            }
            finally
            {
                isApplyingRemoteCamera = false;
            }
        }

        private static Transform ResolveRemoteCameraTarget(
            CutsceneCameraState state)
        {
            WorldGameObject targetWgo = null;
            if (state.TargetUniqueId != 0L)
            {
                targetWgo = WorldMap.GetWorldGameObjectByUniqueId(
                    state.TargetUniqueId,
                    false);
            }

            if (targetWgo == null &&
                !string.IsNullOrEmpty(state.TargetCustomTag))
            {
                targetWgo = WorldMap.GetWorldGameObjectByCustomTag(
                    state.TargetCustomTag,
                    true);
            }

            if (targetWgo == null &&
                !string.IsNullOrEmpty(state.TargetObjId))
            {
                targetWgo = WorldMap.GetWorldGameObjectByObjId(
                    state.TargetObjId,
                    true);
            }

            if (targetWgo != null)
            {
                if (!string.IsNullOrEmpty(state.TargetRelativePath))
                {
                    Transform child = targetWgo.transform.Find(
                        state.TargetRelativePath);
                    if (child != null)
                        return child;
                }
                return targetWgo.transform;
            }

            if (!string.IsNullOrEmpty(state.TargetName))
            {
                GameObject namedTarget =
                    GameObject.Find(state.TargetName);
                if (namedTarget != null)
                    return namedTarget.transform;
            }

            if (remoteCameraFallbackTarget == null)
            {
                remoteCameraFallbackTarget =
                    new GameObject("Remote Cutscene Camera Target");
                remoteCameraFallbackTarget.hideFlags =
                    HideFlags.HideAndDontSave;
            }
            remoteCameraFallbackTarget.transform.position =
                state.TargetPosition;
            return remoteCameraFallbackTarget.transform;
        }

        private static string GetRelativeTransformPath(
            Transform root,
            Transform target)
        {
            if (root == null || target == null || root == target)
                return "";

            var names = new List<string>();
            Transform current = target;
            while (current != null && current != root)
            {
                names.Add(current.name);
                current = current.parent;
            }

            if (current != root)
                return "";

            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static void RestoreRemoteCutsceneCamera(string reason)
        {
            if (!remoteCameraApplied)
                return;

            isApplyingRemoteCamera = true;
            try
            {
                CameraTools.CameraFlyBack();
                CoopMod.Logger.LogInfo(
                    $"[CutsceneSync] Restored camera after remote cutscene " +
                    $"({reason})");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[CutsceneSync] Failed to restore remote cutscene camera: " +
                    $"{ex.Message}");
            }
            finally
            {
                remoteCameraApplied = false;
                isApplyingRemoteCamera = false;
            }
        }

        #endregion

        #region Receive Handler

        /// <summary>
        /// Called when the remote player's craft/WGO action triggered a FlowScript.
        /// Opens a live observation session. The local player may attach immediately or
        /// later, always at the triggering player's current position and cutscene state.
        /// </summary>
        private static void OnCutsceneSyncReceived(CSteamID senderID, string scriptName, Vector3 originWorldPos, byte facing, string originZoneId, bool hasScope)
        {
            if (string.IsNullOrEmpty(scriptName)) return;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled) return;

            string senderName = SteamFriends.GetFriendPersonaName(senderID);
            PruneCompletedDeferredCutscenes();
            if (WasDeferredCutsceneCompleted(senderID, scriptName, originWorldPos, originZoneId))
            {
                CoopMod.Logger.LogInfo($"[CutsceneSync] Remote player '{senderName}' re-sent completed FlowScript \"{scriptName}\" — ignoring duplicate");
                return;
            }

            if (IsMatchingRemoteCutscene(remoteCutsceneSession, senderID, scriptName))
            {
                remoteCutsceneSession.Facing = facing;
                CoopMod.Logger.LogInfo($"[CutsceneSync] Remote player '{senderName}' re-sent active FlowScript \"{scriptName}\" — keeping current live session");
                return;
            }

            if (remoteCutsceneSession != null)
            {
                if (remoteCutsceneSession.IsParticipating &&
                    !remoteCutsceneSession.MirrorsLocalExecution)
                {
                    RestoreRemoteCutsceneCamera(
                        "remote cutscene session replaced");
                    onlineCoop.EndObservedCutsceneFollow("remote cutscene session replaced");
                    UnlockLocalPlayerAfterCutscene();
                }

                CoopMod.Logger.LogWarning($"[CutsceneSync] Replacing unfinished live FlowScript \"{remoteCutsceneSession.ScriptName}\" with \"{scriptName}\"");
            }

            remoteNpcVisualTail = null;

            remoteCutsceneSession = new RemoteCutsceneSession
            {
                SenderID = senderID,
                ScriptName = scriptName,
                OriginWorldPos = originWorldPos,
                Facing = facing,
                OriginZoneId = originZoneId ?? "",
                StartedAt = Time.realtimeSinceStartup
            };

            if (IsGerryDigUpScript(scriptName))
            {
                ApplyGerryDigUpProgression(CreateGerryState(remoteCutsceneSession), "live remote cutscene session");
            }

            CoopMod.Logger.LogInfo($"[CutsceneSync] Remote player '{senderName}' started live FlowScript \"{scriptName}\" — local player may join in progress");
            TryJoinRemoteCutsceneSession("cutscene trigger received", hasScope);
        }

        internal static void LockLocalPlayerForCutscene()
        {
            try
            {
                var pc = MainGame.me?.player_char;
                if (pc != null) pc.control_enabled = false;
                GS.SetPlayerEnable(false, true);
                CoopMod.Logger.LogInfo("[CutsceneSync] Locked local player for observed cutscene (control disabled + letterbox)");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[CutsceneSync] Failed to lock player for cutscene: {ex.Message}");
            }
        }

        internal static void LockLocalPlayerForObservedDialogue()
        {
            try
            {
                var pc = MainGame.me?.player_char;
                if (pc != null) pc.control_enabled = false;
                GS.SetPlayerEnable(false, false);
                CoopMod.Logger.LogInfo(
                    "[CutsceneSync] Locked local player for observed dialogue (control disabled, no letterbox)");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[CutsceneSync] Failed to lock player for observed dialogue: {ex.Message}");
            }
        }

        internal static void UnlockLocalPlayerAfterCutscene()
        {
            try
            {
                var pc = MainGame.me?.player_char;
                if (pc != null)
                {
                    pc.StopMovement();
                    pc.astar?.Clear();
                    pc.player_controlled_by_script = false;
                    pc.can_be_locally_controlled = true;
                    pc.control_enabled = true;
                }
                GS.SetPlayerEnable(true, true);
                CoopMod.Logger.LogInfo("[CutsceneSync] Unlocked local player after observed cutscene");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[CutsceneSync] Failed to unlock player: {ex.Message}");
            }
        }

        private static void OnCutsceneCompleteReceived(CSteamID senderID, string scriptName, Vector3 originWorldPos, string originZoneId)
        {
            if (string.IsNullOrEmpty(scriptName)) return;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled) return;

            RemoteCutsceneSession session = IsMatchingRemoteCutscene(remoteCutsceneSession, senderID, scriptName)
                ? remoteCutsceneSession
                : null;

            if (remoteCutsceneSession != null && session == null)
            {
                CoopMod.Logger.LogWarning($"[CutsceneSync] Ignoring completion for stale FlowScript \"{scriptName}\" while \"{remoteCutsceneSession.ScriptName}\" is active");
                return;
            }

            if (session?.IsParticipating == true &&
                !session.MirrorsLocalExecution)
            {
                RestoreRemoteCutsceneCamera("remote cutscene completed");
                onlineCoop.EndObservedCutsceneFollow("remote cutscene completed");
                UnlockLocalPlayerAfterCutscene();
            }

            if (session != null && !session.MirrorsLocalExecution)
            {
                // Presentation is over, but the authoritative actor may still be
                // walking to an exit/despawn point. Keep accepting only its NPC
                // visual epoch without treating the player as cutscene-bound.
                remoteNpcVisualTail = new RemoteNpcVisualTail
                {
                    SenderID = senderID,
                    StartedAt = Time.realtimeSinceStartup
                };
            }

            if (IsGerryDigUpScript(scriptName))
            {
                var gerryState = session != null
                    ? CreateGerryState(session)
                    : new PendingRemoteCutscene
                    {
                        SenderID = senderID,
                        ScriptName = scriptName,
                        OriginWorldPos = originWorldPos,
                        OriginZoneId = originZoneId ?? ""
                    };
                ApplyGerryDigUpFinalWorldState(gerryState, "live remote cutscene completed");
            }

            RememberCompletedDeferredCutscene(senderID, scriptName, originWorldPos, originZoneId);
            if (session != null)
            {
                remoteCutsceneSession = null;
            }
            string participation = session?.MirrorsLocalExecution == true
                ? "remote finished; mirrored local execution will consume queued dialogue advances"
                : session?.IsParticipating == true
                    ? "detached local participant and restored control"
                    : "closed live session without local participation";
            CoopMod.Logger.LogInfo($"[CutsceneSync] Remote player completed FlowScript \"{scriptName}\" — {participation}");
        }

        private static bool RunRemoteFlowScript(string scriptName)
        {
            if (string.IsNullOrEmpty(scriptName)) return false;

            isProcessingRemote = true;
            try
            {
                BeginGerryDigUpCutsceneIfNeeded(scriptName, "remote replay");
                CustomFlowScript flowScript = GS.RunFlowScript(scriptName, null);
                if (flowScript != null)
                {
                    CoopMod.Logger.LogInfo($"[CutsceneSync] ✓ FlowScript \"{scriptName}\" started successfully");
                    return true;
                }

                EndGerryDigUpCutsceneIfActive("remote replay failed");
                CoopMod.Logger.LogWarning($"[CutsceneSync] FlowScript \"{scriptName}\" returned null (asset not found?)");
                return false;
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[CutsceneSync] Error running remote FlowScript \"{scriptName}\": {ex}");
                return false;
            }
            finally
            {
                isProcessingRemote = false;
            }
        }

        public static void TickRemoteCutsceneSession()
        {
            PruneCompletedDeferredCutscenes();
            PruneLocalSyncedCutscenes();
            TickPendingLocalSyncedCutsceneCompletions();

            if (remoteNpcVisualTail != null &&
                Time.realtimeSinceStartup - remoteNpcVisualTail.StartedAt >=
                    LOCAL_ACTOR_COMPLETION_MAX_WAIT_SECONDS)
            {
                CoopMod.Logger.LogInfo(
                    "[CutsceneSync] Remote NPC visual tail expired");
                remoteNpcVisualTail = null;
            }

            var session = remoteCutsceneSession;
            if (session == null)
            {
                return;
            }

            if (!session.IsParticipating)
            {
                TryJoinRemoteCutsceneSession("live proximity update", true);
            }
        }

        public static bool ShouldDeferCutsceneWalk(CSteamID senderID, Vector3 originWorldPos, byte facing)
        {
            var session = remoteCutsceneSession;
            if (session != null && session.SenderID == senderID)
            {
                session.Facing = facing;
                if (!session.IsParticipating)
                {
                    CoopMod.Logger.LogInfo($"[CutsceneSync] Ignoring trigger-position CutsceneWalkTo for live FlowScript \"{session.ScriptName}\" until the players meet");
                    return true;
                }

                if (session.JoinWalkStarted)
                {
                    CoopMod.Logger.LogInfo($"[CutsceneSync] Ignoring stale trigger-position CutsceneWalkTo for live FlowScript \"{session.ScriptName}\"; join already targets the remote player's current position");
                    return true;
                }
            }

            var pending = pendingRemoteCutscene;
            if (pending != null && pending.SenderID == senderID)
            {
                pending.OriginWorldPos = originWorldPos;
                pending.Facing = facing;
                CoopMod.Logger.LogInfo($"[CutsceneSync] Deferring CutsceneWalkTo for pending FlowScript \"{pending.ScriptName}\"");
                return true;
            }

            if (WasDeferredCutsceneCompleted(senderID, null, originWorldPos, ""))
            {
                // Cutscene already completed — but the player still needs to walk to
                // the cutscene position so they're in the right place for follow-up
                // interactions (e.g. the donkey after Gerry). Don't defer the walk.
                CoopMod.Logger.LogInfo("[CutsceneSync] Cutscene already completed — allowing walk to cutscene position");
                return false;
            }

            if (!ShouldActivateScopedCutscene(originWorldPos, "", out string reason))
            {
                CoopMod.Logger.LogInfo($"[CutsceneSync] Ignoring out-of-scope CutsceneWalkTo ({reason})");
                return true;
            }

            return false;
        }

        public static bool TryBufferRemoteDialogueAdvance(CSteamID senderID)
        {
            if (IsDeferredCutsceneCatchUpActive(senderID))
            {
                deferredCatchUpDialogueOps.Enqueue(new DeferredDialogueOp { Type = DeferredDialogueOpType.Advance });
                CoopMod.Logger.LogInfo($"[CutsceneSync] Queued live dialogue advance while catching up deferred FlowScript \"{deferredCatchUpScriptName}\" (ops={deferredCatchUpDialogueOps.Count})");
                return true;
            }

            var pending = pendingRemoteCutscene;
            if (pending == null || pending.SenderID != senderID)
            {
                return false;
            }

            pending.BufferedDialogueOps.Add(new DeferredDialogueOp { Type = DeferredDialogueOpType.Advance });
            CoopMod.Logger.LogInfo($"[CutsceneSync] Buffered dialogue advance for deferred FlowScript \"{pending.ScriptName}\" (ops={pending.BufferedDialogueOps.Count})");
            return true;
        }

        public static bool TryBufferRemoteDialogueChoice(CSteamID senderID, int choiceIndex, string choiceText)
        {
            if (IsDeferredCutsceneCatchUpActive(senderID))
            {
                deferredCatchUpDialogueOps.Enqueue(new DeferredDialogueOp
                {
                    Type = DeferredDialogueOpType.Choice,
                    ChoiceIndex = choiceIndex,
                    ChoiceText = choiceText
                });
                CoopMod.Logger.LogInfo($"[CutsceneSync] Queued live choice {choiceIndex} while catching up deferred FlowScript \"{deferredCatchUpScriptName}\" (ops={deferredCatchUpDialogueOps.Count})");
                return true;
            }

            var pending = pendingRemoteCutscene;
            if (pending == null || pending.SenderID != senderID)
            {
                return false;
            }

            pending.BufferedDialogueOps.Add(new DeferredDialogueOp
            {
                Type = DeferredDialogueOpType.Choice,
                ChoiceIndex = choiceIndex,
                ChoiceText = choiceText
            });
            CoopMod.Logger.LogInfo($"[CutsceneSync] Buffered dialogue choice {choiceIndex} for deferred FlowScript \"{pending.ScriptName}\" (ops={pending.BufferedDialogueOps.Count})");
            return true;
        }

        public static bool TryEndDeferredRemoteCutscene(CSteamID senderID)
        {
            var pending = pendingRemoteCutscene;
            if (pending == null || pending.SenderID != senderID)
            {
                return false;
            }

            if (IsGerryDigUpScript(pending.ScriptName))
            {
                MarkGerryDeferredCutsceneCompleted(pending, "ended dialogue on remote");
                return true;
            }

            MarkDeferredCutsceneCompleted(pending, "ended dialogue on remote");
            return true;
        }

        private static void WrapCutsceneFinishedCallback(LocalSyncedCutscene localCutscene, ref CustomFlowScript.OnFinishedDelegate onFinished)
        {
            if (localCutscene == null)
            {
                return;
            }

            var original = onFinished;
            onFinished = scriptName =>
            {
                try
                {
                    CompleteLocalSyncedCutscene(localCutscene, "FlowScript finished");
                }
                finally
                {
                    original?.Invoke(scriptName);
                }
            };
        }

        private static void CompleteLocalSyncedCutscenes(string reason)
        {
            PruneLocalSyncedCutscenes();

            for (int i = localSyncedCutscenes.Count - 1; i >= 0; i--)
            {
                CompleteLocalSyncedCutscene(localSyncedCutscenes[i], reason);
            }
        }

        private static void CompleteLocalSyncedCutscene(LocalSyncedCutscene localCutscene, string reason)
        {
            if (localCutscene == null || localCutscene.CompletionSent)
            {
                return;
            }

            bool actorStillMoving =
                !localCutscene.MirrorsRemoteExecution &&
                NpcVisualSync.Instance?.AreLocalCutsceneActorsMoving() == true;
            if (actorStillMoving)
            {
                if (localCutscene.CompletionRequestedAt <= 0f)
                {
                    localCutscene.CompletionRequestedAt =
                        Time.realtimeSinceStartup;
                    localCutscene.CompletionReason = reason ?? "completion requested";
                    localCutscene.CompletionSettledAt = 0f;
                    localCutscene.VisualTailActive = true;
                    CompleteLocalSyncedCutscenePresentation(
                        localCutscene,
                        localCutscene.CompletionReason);
                    CoopMod.Logger.LogInfo(
                        $"[CutsceneSync] FlowScript \"{localCutscene.ScriptName}\" " +
                        "restored player control while an enrolled NPC is still moving; " +
                        "presentation completed and the actor will continue in a visual-only tail");
                }
                return;
            }

            FinalizeLocalSyncedCutscene(localCutscene, reason);
        }

        private static void CompleteLocalSyncedCutscenePresentation(
            LocalSyncedCutscene localCutscene,
            string reason)
        {
            if (localCutscene == null || localCutscene.CompletionSent)
                return;

            localCutscene.CompletionSent = true;
            if (!localCutscene.SuppressCompletionBroadcast)
            {
                SteamP2PManager.Instance?.SendCutsceneComplete(
                    localCutscene.ScriptName,
                    localCutscene.OriginWorldPos,
                    localCutscene.OriginZoneId);
            }
            if (localCutscene.MirrorsRemoteExecution)
                DialogueSync.Instance?.NotifyMirroredLocalCutsceneFinished();

            string ownership = localCutscene.SuppressCompletionBroadcast
                ? "mirrored remote"
                : "local synced";
            CoopMod.Logger.LogInfo(
                $"[CutsceneSync] {ownership} FlowScript " +
                $"\"{localCutscene.ScriptName}\" presentation completed ({reason})");
        }

        private static void TickPendingLocalSyncedCutsceneCompletions()
        {
            float now = Time.realtimeSinceStartup;
            for (int i = localSyncedCutscenes.Count - 1; i >= 0; i--)
            {
                LocalSyncedCutscene local = localSyncedCutscenes[i];
                if (!local.VisualTailActive ||
                    local.CompletionRequestedAt <= 0f)
                {
                    continue;
                }

                float waitingFor = now - local.CompletionRequestedAt;
                bool moving =
                    NpcVisualSync.Instance?.AreLocalCutsceneActorsMoving() == true;
                if (moving &&
                    waitingFor < LOCAL_ACTOR_COMPLETION_MAX_WAIT_SECONDS)
                {
                    local.CompletionSettledAt = 0f;
                    continue;
                }

                if (!moving &&
                    waitingFor < LOCAL_ACTOR_COMPLETION_MAX_WAIT_SECONDS)
                {
                    if (local.CompletionSettledAt <= 0f)
                    {
                        local.CompletionSettledAt = now;
                        continue;
                    }
                    if (now - local.CompletionSettledAt <
                        LOCAL_ACTOR_COMPLETION_SETTLE_SECONDS)
                    {
                        continue;
                    }
                }

                string completionReason =
                    waitingFor >= LOCAL_ACTOR_COMPLETION_MAX_WAIT_SECONDS
                        ? "actor settle timeout"
                        : "enrolled actors settled";
                local.VisualTailActive = false;
                localSyncedCutscenes.Remove(local);
                CoopMod.Logger.LogInfo(
                    $"[CutsceneSync] FlowScript \"{local.ScriptName}\" " +
                    $"NPC visual tail ended ({completionReason})");
            }
        }

        private static void FinalizeLocalSyncedCutscene(
            LocalSyncedCutscene localCutscene,
            string reason)
        {
            if (localCutscene == null || localCutscene.CompletionSent)
                return;

            CompleteLocalSyncedCutscenePresentation(localCutscene, reason);
            localCutscene.VisualTailActive = false;
            localSyncedCutscenes.Remove(localCutscene);
        }

        private static bool HasActiveLocalCutscenePresentation()
        {
            for (int i = 0; i < localSyncedCutscenes.Count; i++)
            {
                if (!localSyncedCutscenes[i].CompletionSent)
                    return true;
            }

            return false;
        }

        private static void MarkDeferredCutsceneCompleted(PendingRemoteCutscene pending, string reason)
        {
            if (pending == null)
            {
                return;
            }

            int bufferedOps = pending.BufferedDialogueOps.Count;
            RememberCompletedDeferredCutscene(pending.SenderID, pending.ScriptName, pending.OriginWorldPos, pending.OriginZoneId);

            if (pendingRemoteCutscene == pending)
            {
                pendingRemoteCutscene = null;
            }

            CoopMod.Logger.LogInfo($"[CutsceneSync] Remote FlowScript \"{pending.ScriptName}\" {reason} while deferred — skipping replay and dropping {bufferedOps} buffered dialogue ops");
        }

        private static void MarkGerryDeferredCutsceneCompleted(PendingRemoteCutscene pending, string reason)
        {
            if (pending == null)
            {
                return;
            }

            pending.RemoteCompleted = true;
            ApplyGerryDigUpFinalWorldState(pending, reason);
            RememberCompletedDeferredCutscene(pending.SenderID, pending.ScriptName, pending.OriginWorldPos, pending.OriginZoneId);

            if (pendingRemoteCutscene == pending)
            {
                pendingRemoteCutscene = null;
            }

            CoopMod.Logger.LogInfo($"[CutsceneSync] Remote Gerry FlowScript {reason} while deferred — progression already synced, not replaying completed cinematic, dropped bufferedOps={pending.BufferedDialogueOps.Count}");
        }

        private static void RememberCompletedDeferredCutscene(CSteamID senderID, string scriptName, Vector3 originWorldPos, string originZoneId)
        {
            PruneCompletedDeferredCutscenes();

            for (int i = 0; i < completedDeferredCutscenes.Count; i++)
            {
                var completed = completedDeferredCutscenes[i];
                if (completed.SenderID == senderID &&
                    IsSameCutscene(completed.ScriptName, completed.OriginWorldPos, completed.OriginZoneId, scriptName, originWorldPos, originZoneId))
                {
                    completed.CompletedAt = Time.realtimeSinceStartup;
                    return;
                }
            }

            completedDeferredCutscenes.Add(new CompletedDeferredCutscene
            {
                SenderID = senderID,
                ScriptName = scriptName,
                OriginWorldPos = originWorldPos,
                OriginZoneId = originZoneId ?? "",
                CompletedAt = Time.realtimeSinceStartup
            });
        }

        private static bool WasDeferredCutsceneCompleted(CSteamID senderID, string scriptName, Vector3 originWorldPos, string originZoneId)
        {
            PruneCompletedDeferredCutscenes();

            for (int i = 0; i < completedDeferredCutscenes.Count; i++)
            {
                var completed = completedDeferredCutscenes[i];
                if (completed.SenderID == senderID &&
                    IsSameCutscene(completed.ScriptName, completed.OriginWorldPos, completed.OriginZoneId, scriptName, originWorldPos, originZoneId))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSameCutscene(string knownScriptName, Vector3 knownOriginWorldPos, string knownOriginZoneId, string scriptName, Vector3 originWorldPos, string originZoneId)
        {
            bool hasScriptName = !string.IsNullOrEmpty(scriptName);
            if (hasScriptName &&
                !string.IsNullOrEmpty(knownScriptName) &&
                !string.Equals(knownScriptName, scriptName, System.StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            bool hasKnownZone = !string.IsNullOrEmpty(knownOriginZoneId);
            bool hasZone = !string.IsNullOrEmpty(originZoneId);
            if (hasKnownZone && hasZone &&
                !string.Equals(knownOriginZoneId, originZoneId, System.StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            bool hasKnownOrigin = knownOriginWorldPos != Vector3.zero;
            bool hasOrigin = originWorldPos != Vector3.zero;
            if (hasKnownOrigin && hasOrigin)
            {
                return Distance2D(knownOriginWorldPos, originWorldPos) <= CUTSCENE_ORIGIN_MATCH_TOLERANCE;
            }

            return hasScriptName;
        }

        private static void PruneCompletedDeferredCutscenes()
        {
            float now = Time.realtimeSinceStartup;
            for (int i = completedDeferredCutscenes.Count - 1; i >= 0; i--)
            {
                if (now - completedDeferredCutscenes[i].CompletedAt > COMPLETED_DEFERRED_CUTSCENE_TTL)
                {
                    completedDeferredCutscenes.RemoveAt(i);
                }
            }
        }

        private static void PruneLocalSyncedCutscenes()
        {
            float now = Time.realtimeSinceStartup;
            for (int i = localSyncedCutscenes.Count - 1; i >= 0; i--)
            {
                var localCutscene = localSyncedCutscenes[i];
                if ((localCutscene.CompletionSent &&
                     !localCutscene.VisualTailActive) ||
                    now - localCutscene.StartedAt >
                        LOCAL_SYNCED_CUTSCENE_TTL)
                {
                    localSyncedCutscenes.RemoveAt(i);
                }
            }
        }

        private static bool HasActiveLocalSyncedCutscene(
            string scriptName)
        {
            if (string.IsNullOrEmpty(scriptName))
                return false;

            PruneLocalSyncedCutscenes();
            for (int i = 0; i < localSyncedCutscenes.Count; i++)
            {
                LocalSyncedCutscene local = localSyncedCutscenes[i];
                if (!local.CompletionSent &&
                    string.Equals(
                        local.ScriptName,
                        scriptName,
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void MarkActiveLocalCutsceneMirrored(string scriptName)
        {
            for (int i = 0; i < localSyncedCutscenes.Count; i++)
            {
                LocalSyncedCutscene local = localSyncedCutscenes[i];
                if (!local.CompletionSent &&
                    string.Equals(
                        local.ScriptName,
                        scriptName,
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    local.MirrorsRemoteExecution = true;
                    return;
                }
            }
        }

        internal static bool HasActiveMirroredLocalCutscene()
        {
            PruneLocalSyncedCutscenes();
            for (int i = 0; i < localSyncedCutscenes.Count; i++)
            {
                LocalSyncedCutscene local = localSyncedCutscenes[i];
                if (!local.CompletionSent && local.MirrorsRemoteExecution)
                    return true;
            }

            return false;
        }

        internal static bool IsAuthoritativeNpcAnimationWindowActive()
        {
            PruneLocalSyncedCutscenes();
            if (isGerryDigUpCutsceneActive)
                return true;

            for (int i = 0; i < localSyncedCutscenes.Count; i++)
            {
                if ((!localSyncedCutscenes[i].CompletionSent ||
                     localSyncedCutscenes[i].VisualTailActive) &&
                    !localSyncedCutscenes[i].MirrorsRemoteExecution)
                    return true;
            }

            return false;
        }

        internal static bool IsNpcNetworkPuppetWindowActive()
        {
            RemoteCutsceneSession session = remoteCutsceneSession;
            bool activeSession =
                session != null &&
                !session.MirrorsLocalExecution &&
                Time.realtimeSinceStartup - session.StartedAt <=
                    LOCAL_SYNCED_CUTSCENE_TTL;
            return activeSession || IsRemoteNpcVisualTailActive();
        }

        internal static bool HasRemoteNpcVisualAuthority()
        {
            RemoteCutsceneSession session = remoteCutsceneSession;
            bool activeSession =
                session != null &&
                !session.MirrorsLocalExecution &&
                Time.realtimeSinceStartup - session.StartedAt <=
                    LOCAL_SYNCED_CUTSCENE_TTL;
            return activeSession || IsRemoteNpcVisualTailActive();
        }

        internal static bool IsRemoteNpcVisualAuthority(CSteamID senderID)
        {
            if (remoteCutsceneSession != null &&
                !remoteCutsceneSession.MirrorsLocalExecution &&
                Time.realtimeSinceStartup -
                    remoteCutsceneSession.StartedAt <=
                    LOCAL_SYNCED_CUTSCENE_TTL &&
                remoteCutsceneSession.SenderID == senderID)
            {
                return true;
            }

            return IsRemoteNpcVisualTailActive() &&
                   remoteNpcVisualTail.SenderID == senderID;
        }

        internal static bool HasLocalNpcVisualAuthority()
        {
            PruneLocalSyncedCutscenes();
            if (isGerryDigUpCutsceneActive)
                return true;

            for (int i = 0; i < localSyncedCutscenes.Count; i++)
            {
                LocalSyncedCutscene local = localSyncedCutscenes[i];
                if ((!local.CompletionSent || local.VisualTailActive) &&
                    !local.MirrorsRemoteExecution)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsRemoteNpcVisualTailActive()
        {
            return remoteNpcVisualTail != null &&
                   Time.realtimeSinceStartup - remoteNpcVisualTail.StartedAt <
                       LOCAL_ACTOR_COMPLETION_MAX_WAIT_SECONDS;
        }

        internal static void NotifyRemoteNpcVisualEpochEnded(
            CSteamID senderID)
        {
            if (remoteNpcVisualTail == null ||
                remoteNpcVisualTail.SenderID != senderID)
            {
                return;
            }

            remoteNpcVisualTail = null;
            CoopMod.Logger.LogInfo(
                "[CutsceneSync] Remote NPC visual tail completed");
        }

        internal static bool IsLocalCutsceneNpcActor(WorldGameObject wgo)
        {
            return isGerryDigUpCutsceneActive &&
                   wgo != null &&
                   (string.Equals(
                        wgo.obj_id,
                        GERRY_WGO_ID,
                        System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        wgo.obj_id,
                        DONKEY_WGO_ID,
                        System.StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryJoinRemoteCutsceneSession(string reason, bool requireProximity)
        {
            var session = remoteCutsceneSession;
            var onlineCoop = OnlineCoopManager.Instance;
            var remotePlayer = onlineCoop?.GetRemotePlayer(session?.SenderID ?? CSteamID.Nil);
            if (session == null ||
                session.IsParticipating ||
                onlineCoop == null ||
                !onlineCoop.IsOnlineCoopEnabled ||
                !onlineCoop.IsRemotePlayer(session.SenderID) ||
                remotePlayer == null)
            {
                return false;
            }

            // Both peers can legitimately begin the same global cutscene before
            // either trigger packet arrives (the new-game Red Eye sequence is
            // one example). In that case the local FlowScript already owns
            // movement, facing, camera, and player locking. Starting an observed
            // join on top of it reapplies the trigger packet's pre-cutscene
            // facing and can override later Flow_SetCharacterDirection nodes.
            if (HasActiveLocalSyncedCutscene(session.ScriptName))
            {
                session.IsParticipating = true;
                session.JoinWalkStarted = true;
                session.MirrorsLocalExecution = true;
                MarkActiveLocalCutsceneMirrored(session.ScriptName);
                EnsureSharedCutsceneDialogueOwner();
                CoopMod.Logger.LogInfo(
                    $"[CutsceneSync] Coalesced reciprocal FlowScript " +
                    $"\"{session.ScriptName}\" with the active local execution");
                return true;
            }

            Vector3 liveRemotePosition = remotePlayer.transform.position;
            if (requireProximity &&
                !ShouldActivateScopedCutscene(liveRemotePosition, "", out string scopeReason))
            {
                if (!session.ScopeRejectionLogged)
                {
                    session.ScopeRejectionLogged = true;
                    CoopMod.Logger.LogInfo(
                        $"[CutsceneSync] Did not attach the local avatar to remote FlowScript \"{session.ScriptName}\" " +
                        $"({scopeReason}); remote presentation and shared progression remain active");
                }
                return false;
            }

            // Some shared global cutscenes start independently on every peer. Mirror
            // only those known-global scripts while suppressing their completion echo.
            // Player-bound interaction cutscenes must remain owned by the initiator.
            if (RequiresLocalFlowScriptMirror(session.ScriptName) &&
                TryStartMirroredRemoteFlowScript(session))
            {
                return true;
            }

            try
            {
                var remoteCharacter = remotePlayer.components?.character;
                if (remoteCharacter != null)
                {
                    session.Facing = (byte)remoteCharacter.anim_direction;
                }
            }
            catch { }

            session.IsParticipating = true;
            session.JoinWalkStarted = true;
            LockLocalPlayerForCutscene();
            onlineCoop.BeginObservedCutsceneFollow(session.SenderID);
            onlineCoop.StartLiveCutsceneJoinWalk(session.SenderID, liveRemotePosition, session.Facing);
            if (session.CameraState != null)
            {
                ApplyRemoteCutsceneCamera(session.CameraState);
            }
            float distance = Distance2D(MainGame.me?.player?.transform.position ?? liveRemotePosition, liveRemotePosition);
            CoopMod.Logger.LogInfo($"[CutsceneSync] Joined live remote FlowScript \"{session.ScriptName}\" at initiator position {liveRemotePosition} ({reason}, distance={distance:F1})");
            return true;
        }

        private static bool TryStartMirroredRemoteFlowScript(RemoteCutsceneSession session)
        {
            var localCutscene = new LocalSyncedCutscene
            {
                ScriptName = session.ScriptName,
                OriginWorldPos = session.OriginWorldPos,
                OriginZoneId = session.OriginZoneId ?? "",
                StartedAt = Time.realtimeSinceStartup,
                MirrorsRemoteExecution = true,
                SuppressCompletionBroadcast = true
            };

            // Register ownership before GS.RunFlowScript: the script can disable or
            // restore player control synchronously during startup.
            localSyncedCutscenes.Add(localCutscene);
            session.IsParticipating = true;
            session.JoinWalkStarted = true;
            session.MirrorsLocalExecution = true;
            EnsureSharedCutsceneDialogueOwner();

            if (RunRemoteFlowScript(session.ScriptName))
            {
                CoopMod.Logger.LogInfo(
                    $"[CutsceneSync] Started required local mirror for remote " +
                    $"FlowScript \"{session.ScriptName}\"");
                return true;
            }

            localSyncedCutscenes.Remove(localCutscene);
            session.IsParticipating = false;
            session.JoinWalkStarted = false;
            session.MirrorsLocalExecution = false;
            CoopMod.Logger.LogWarning(
                $"[CutsceneSync] Required local mirror for \"{session.ScriptName}\" failed; " +
                "falling back to live observation");
            return false;
        }

        private static bool RequiresLocalFlowScriptMirror(string scriptName)
        {
            return string.Equals(
                scriptName,
                RED_EYE_INTRO_SCRIPT,
                System.StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ShouldKeepLocalPlayerLocked(CSteamID senderID)
        {
            return remoteCutsceneSession != null &&
                   remoteCutsceneSession.IsParticipating;
        }

        internal static bool HasLiveRemoteCutsceneSession(CSteamID senderID)
        {
            return remoteCutsceneSession != null &&
                   remoteCutsceneSession.SenderID == senderID;
        }

        internal static bool IsAwaitingLocalParticipation()
        {
            return remoteCutsceneSession != null &&
                   !remoteCutsceneSession.IsParticipating;
        }

        private static bool IsMatchingRemoteCutscene(RemoteCutsceneSession cutscene, CSteamID senderID, string scriptName)
        {
            return cutscene != null &&
                   cutscene.SenderID == senderID &&
                   string.Equals(cutscene.ScriptName, scriptName, System.StringComparison.OrdinalIgnoreCase);
        }

        private static PendingRemoteCutscene CreateGerryState(RemoteCutsceneSession session)
        {
            return new PendingRemoteCutscene
            {
                SenderID = session.SenderID,
                ScriptName = session.ScriptName,
                OriginWorldPos = session.OriginWorldPos,
                Facing = session.Facing,
                OriginZoneId = session.OriginZoneId,
                ReceivedAt = session.StartedAt
            };
        }

        private static IEnumerator FastForwardBufferedDialogue(List<DeferredDialogueOp> bufferedOps, string scriptName)
        {
            // Let the FlowScript create its first bubble/choice before applying queued inputs.
            yield return null;

            for (int i = 0; i < bufferedOps.Count; i++)
            {
                var op = bufferedOps[i];
                float deadline = Time.realtimeSinceStartup + DEFERRED_FAST_FORWARD_STEP_TIMEOUT;
                while (!HasActiveDialogueUI() && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                if (op.Type == DeferredDialogueOpType.Choice)
                {
                    DialogueSync.Instance?.ApplyRemoteDialogueChoiceNow(op.ChoiceIndex);
                }
                else
                {
                    DialogueSync.Instance?.ApplyRemoteDialogueAdvanceNow();
                }

                // Process next op immediately — no artificial delay.
                // The while loop above already waits for the dialogue UI to appear.
                yield return null;
            }

            CoopMod.Logger.LogInfo($"[CutsceneSync] Fast-forwarded {bufferedOps.Count} deferred dialogue ops for \"{scriptName}\"");
        }

        private static bool HasActiveDialogueUI()
        {
            if (SpeechBubbleGUI.all != null && SpeechBubbleGUI.all.Count > 0)
            {
                return true;
            }

            var multiAnswer = GUIElements.me?.multi_answer;
            return multiAnswer != null && multiAnswer.gameObject.activeInHierarchy;
        }

        public static bool ConsumeDeferredCatchUpDialogueCaptureSuppression()
        {
            if (suppressedDeferredCatchUpDialogueCaptures <= 0)
            {
                return false;
            }

            suppressedDeferredCatchUpDialogueCaptures--;
            return true;
        }

        private static void BeginDeferredCutsceneCatchUp(List<DeferredDialogueOp> bufferedOps, string scriptName, CSteamID senderID)
        {
            deferredCatchUpDialogueOps.Clear();
            if (bufferedOps == null || bufferedOps.Count == 0)
            {
                EndDeferredCutsceneCatchUp("no buffered ops");
                return;
            }

            for (int i = 0; i < bufferedOps.Count; i++)
            {
                deferredCatchUpDialogueOps.Enqueue(bufferedOps[i]);
            }

            deferredCatchUpScriptName = scriptName ?? "";
            deferredCatchUpSenderID = senderID;
            CoopMod.Logger.LogInfo($"[CutsceneSync] Catching up deferred FlowScript \"{deferredCatchUpScriptName}\" from {deferredCatchUpDialogueOps.Count} buffered dialogue ops");
        }

        private static void EndDeferredCutsceneCatchUp(string reason)
        {
            if (deferredCatchUpSenderID == CSteamID.Nil && deferredCatchUpDialogueOps.Count == 0)
            {
                return;
            }

            int remaining = deferredCatchUpDialogueOps.Count;
            string scriptName = deferredCatchUpScriptName;
            deferredCatchUpDialogueOps.Clear();
            deferredCatchUpSenderID = CSteamID.Nil;
            deferredCatchUpScriptName = "";
            CoopMod.Logger.LogInfo($"[CutsceneSync] Deferred FlowScript catch-up ended ({reason}), script=\"{scriptName}\", remainingOps={remaining}");
        }

        private static void CleanupDeferredCutsceneCatchUpIfIdle(string reason)
        {
            if (deferredCatchUpSenderID != CSteamID.Nil && deferredCatchUpDialogueOps.Count == 0)
            {
                EndDeferredCutsceneCatchUp(reason);
            }
        }

        private static bool IsDeferredCutsceneCatchUpActive(CSteamID senderID)
        {
            return deferredCatchUpSenderID != CSteamID.Nil && deferredCatchUpSenderID == senderID;
        }

        private static bool TryPeekDeferredCatchUpOp(DeferredDialogueOpType expectedType, out DeferredDialogueOp op)
        {
            op = null;
            if (deferredCatchUpSenderID == CSteamID.Nil || deferredCatchUpDialogueOps.Count == 0)
            {
                CleanupDeferredCutsceneCatchUpIfIdle("caught up");
                return false;
            }

            op = deferredCatchUpDialogueOps.Peek();
            return op.Type == expectedType;
        }

        private static DeferredDialogueOp DequeueDeferredCatchUpOp()
        {
            DeferredDialogueOp op = deferredCatchUpDialogueOps.Dequeue();
            CleanupDeferredCutsceneCatchUpIfIdle("caught up");
            return op;
        }

        private static string ResolveCatchUpChoiceAnswer(List<AnswerVisualData> answers, DeferredDialogueOp op)
        {
            if (op == null)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(op.ChoiceText))
            {
                return op.ChoiceText;
            }

            if (answers == null || op.ChoiceIndex < 0)
            {
                return null;
            }

            var save = MainGame.me?.save;
            if (save == null)
            {
                return null;
            }

            int visibleIndex = 0;
            for (int i = 0; i < answers.Count; i++)
            {
                AnswerVisualData answer = answers[i];
                if (answer == null || string.IsNullOrEmpty(answer.id))
                {
                    continue;
                }

                bool isLockedPhrase = answer.id[0] == '@' && !save.unlocked_phrases.Contains(answer.id);
                if (isLockedPhrase || save.black_list_of_phrases.Contains(answer.id))
                {
                    continue;
                }

                if (visibleIndex == op.ChoiceIndex)
                {
                    return answer.id;
                }

                visibleIndex++;
            }

            return null;
        }

        private static bool ShouldActivateScopedCutscene(Vector3 originWorldPos, string originZoneId, out string reason)
        {
            reason = "";

            // If a caller used the legacy overload and provided no useful scope, keep the old behavior.
            if (originWorldPos == Vector3.zero && string.IsNullOrEmpty(originZoneId))
            {
                reason = "no scope metadata";
                return true;
            }

            var localPlayer = MainGame.me?.player;
            if (localPlayer == null)
            {
                reason = "local player unavailable";
                return false;
            }

            string localZoneId = GetLocalPlayerZoneId();
            if (!string.IsNullOrEmpty(originZoneId) &&
                !string.Equals(localZoneId, originZoneId, System.StringComparison.OrdinalIgnoreCase))
            {
                reason = $"zone mismatch local='{localZoneId}' origin='{originZoneId}'";
                return false;
            }

            float distance = Distance2D(localPlayer.transform.position, originWorldPos);
            if (distance > CUTSCENE_ACTIVATION_DISTANCE)
            {
                reason = $"distance {distance:F1} > {CUTSCENE_ACTIVATION_DISTANCE:F1}";
                return false;
            }

            reason = $"in scope distance={distance:F1}, zone='{localZoneId}'";
            return true;
        }

        private static void BeginGerryDigUpCutsceneIfNeeded(string scriptName, string reason)
        {
            if (!IsGerryDigUpScript(scriptName))
            {
                return;
            }

            isGerryDigUpCutsceneActive = true;
            gerryDigUpCutsceneStartedAt = Time.realtimeSinceStartup;
            StoreGerryDigUpPlayerSpeedIfNeeded();
            ClampGerryDigUpPlayerMovementSpeed(MainGame.me?.player_char, "cutscene start");
            CoopMod.Logger.LogInfo($"[CutsceneSync] Gerry dig-up movement pacing active ({reason})");
        }

        private static void EndGerryDigUpCutsceneIfActive(string reason)
        {
            if (!isGerryDigUpCutsceneActive)
            {
                return;
            }

            isGerryDigUpCutsceneActive = false;
            gerryDigUpCutsceneStartedAt = 0f;
            RestoreGerryDigUpPlayerSpeedIfNeeded(reason);
            CoopMod.Logger.LogInfo($"[CutsceneSync] Gerry dig-up movement pacing ended ({reason})");
        }

        private static void StoreGerryDigUpPlayerSpeedIfNeeded()
        {
            if (hasGerryDigUpPreCutscenePlayerSpeed)
            {
                return;
            }

            var player = MainGame.me?.player;
            gerryDigUpPreCutscenePlayerSpeed = player?.data?.GetParam("speed", LazyConsts.PLAYER_SPEED) ?? LazyConsts.PLAYER_SPEED;
            hasGerryDigUpPreCutscenePlayerSpeed = true;
        }

        private static void RestoreGerryDigUpPlayerSpeedIfNeeded(string reason)
        {
            if (!hasGerryDigUpPreCutscenePlayerSpeed)
            {
                return;
            }

            float speedToRestore = gerryDigUpPreCutscenePlayerSpeed > 0f ? gerryDigUpPreCutscenePlayerSpeed : LazyConsts.PLAYER_SPEED;
            hasGerryDigUpPreCutscenePlayerSpeed = false;
            gerryDigUpPreCutscenePlayerSpeed = LazyConsts.PLAYER_SPEED;

            try
            {
                var playerChar = MainGame.me?.player_char;
                if (playerChar != null)
                {
                    playerChar.SetSpeed(speedToRestore);
                    CoopMod.Logger.LogInfo($"[CutsceneSync] Restored player speed after Gerry cutscene ({reason}): {speedToRestore:F2}");
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[CutsceneSync] Failed to restore player speed after Gerry cutscene ({reason}): {ex.Message}");
            }
        }

        private static bool IsGerryDigUpCutsceneActive()
        {
            if (!isGerryDigUpCutsceneActive)
            {
                return false;
            }

            if (Time.realtimeSinceStartup - gerryDigUpCutsceneStartedAt > GERRY_DIG_UP_CUTSCENE_SPEED_TIMEOUT)
            {
                EndGerryDigUpCutsceneIfActive("timeout");
                return false;
            }

            return true;
        }

        private static bool IsGerryDigUpScript(string scriptName)
        {
            return string.Equals(scriptName, GERRY_DIG_UP_SCRIPT, System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLocalPlayerMovement(MovementComponent movement)
        {
            var player = MainGame.me?.player;
            return movement?.wgo != null && player != null && movement.wgo == player;
        }

        private static void ClampGerryDigUpPlayerMovementSpeed(MovementComponent movement, string source, bool logWhenChanged = true)
        {
            if (!IsLocalPlayerMovement(movement))
            {
                return;
            }

            var data = movement.wgo.data;
            if (data == null)
            {
                return;
            }

            float currentSpeed = data.GetParam("speed", LazyConsts.PLAYER_SPEED);
            if (currentSpeed <= 0f || Mathf.Abs(currentSpeed - GERRY_DIG_UP_PLAYER_WALK_SPEED) <= 0.01f)
            {
                return;
            }

            data.SetParam("speed", GERRY_DIG_UP_PLAYER_WALK_SPEED);
            if (logWhenChanged)
            {
                CoopMod.Logger.LogInfo($"[CutsceneSync] Gerry scripted player {source} speed clamped: {currentSpeed:F2} -> {GERRY_DIG_UP_PLAYER_WALK_SPEED:F2}");
            }
        }

        private static void ApplyGerryDigUpProgression(PendingRemoteCutscene pending, string reason)
        {
            if (pending == null || pending.ProgressionApplied)
            {
                return;
            }

            try
            {
                EnsureGerryVisible(pending.OriginWorldPos);
                MarkQuestSucceededWithoutScripts(GERRY_DIG_QUEST_ID);
                MainGame.me?.save?.known_npcs?.GetOrCreateNPC(GERRY_NPC_ID);
                MainGame.me?.save?.known_npcs?.GetOrCreateNPC("player")?.SetQuestState(GERRY_PLAYER_TASK_ID, KnownNPC.TaskState.State.Complete);
                GUIElements.me?.relation?.npc_tasks?.Redraw();
                GUIElements.me?.quest_list?.Redraw();
                pending.ProgressionApplied = true;
                CoopMod.Logger.LogInfo($"[CutsceneSync] Gerry dig-up progression synced without cinematic ({reason})");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[CutsceneSync] Failed to sync Gerry dig-up progression ({reason}): {ex.Message}");
            }
        }

        private static void ApplyGerryDigUpFinalWorldState(PendingRemoteCutscene pending, string reason)
        {
            if (pending == null)
            {
                return;
            }

            ApplyGerryDigUpProgression(pending, reason);
            if (pending.FinalWorldStateApplied)
            {
                return;
            }

            pending.FinalWorldStateApplied = true;
            EnsureGerryVisible(ResolveGDPointPosition(GERRY_FINAL_POINT_TAG, GERRY_DIG_UP_FINAL_WORLD_POS), $"post-cutscene final state ({reason})");
            EnsureDonkeyVisibleForGerryDigUp(reason);
            ApplyGerryDigUpDonkeyProgression(reason);
            CoopMod.Logger.LogInfo($"[CutsceneSync] Gerry dig-up final world state synced without replay ({reason})");
        }

        private static Vector3 ResolveGDPointPosition(string gdPointTag, Vector3 fallback)
        {
            try
            {
                GDPoint gdPoint = WorldMap.GetGDPointByGDTag(gdPointTag, true, true);
                if (gdPoint != null)
                {
                    return gdPoint.transform.position;
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[CutsceneSync] Failed to resolve GD point '{gdPointTag}': {ex.Message}");
            }

            return fallback;
        }

        private static void EnsureGerryVisible(Vector3 worldPos, string reason = "out-of-scope progression")
        {
            var save = MainGame.me?.save;
            if (save == null || MainGame.me.world_root == null)
            {
                return;
            }

            Vector3 spawnPos = worldPos != Vector3.zero
                ? worldPos
                : (MainGame.me.player != null ? MainGame.me.player.transform.position : Vector3.zero);
            WorldGameObject gerry = WorldMap.GetWorldGameObjectByObjId(GERRY_WGO_ID, true);
            if (gerry == null)
            {
                gerry = WorldMap.SpawnWGO(MainGame.me.world_root, GERRY_WGO_ID, new Vector3?(spawnPos));
                RefreshScriptedWorldObject(gerry);
                CoopMod.Logger.LogInfo($"[CutsceneSync] Spawned Gerry for {reason} at {spawnPos}");
            }
            else
            {
                gerry.transform.position = spawnPos;
                gerry.gameObject.SetActive(true);
                RefreshScriptedWorldObject(gerry);
                CoopMod.Logger.LogInfo($"[CutsceneSync] Positioned Gerry for {reason} at {spawnPos}");
            }
        }

        private static void EnsureDonkeyVisibleForGerryDigUp(string reason)
        {
            if (MainGame.me?.save == null || MainGame.me.world_root == null)
            {
                return;
            }

            Vector3 spawnPos = ResolveGDPointPosition(DONKEY_CEMETERY_POINT_TAG, GERRY_DIG_UP_FINAL_WORLD_POS);
            GDPoint cemeteryPoint = null;
            try
            {
                cemeteryPoint = WorldMap.GetGDPointByGDTag(DONKEY_CEMETERY_POINT_TAG, true, true);
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[CutsceneSync] Failed to resolve donkey cemetery point: {ex.Message}");
            }

            WorldGameObject donkey = WorldMap.GetWorldGameObjectByCustomTag(DONKEY_CUSTOM_TAG, true);
            if (donkey == null)
            {
                donkey = WorldMap.GetWorldGameObjectByObjId(DONKEY_WGO_ID, true);
            }

            if (donkey == null)
            {
                donkey = WorldMap.SpawnWGO(MainGame.me.world_root, DONKEY_WGO_ID, new Vector3?(spawnPos));
                if (donkey != null)
                {
                    donkey.custom_tag = DONKEY_CUSTOM_TAG;
                }
                CoopMod.Logger.LogInfo($"[CutsceneSync] Spawned donkey for Gerry dig-up final state ({reason}) at {spawnPos}");
            }
            else
            {
                donkey.transform.position = spawnPos;
                donkey.gameObject.SetActive(true);
                CoopMod.Logger.LogInfo($"[CutsceneSync] Positioned donkey for Gerry dig-up final state ({reason}) at {spawnPos}");
            }

            if (donkey == null)
            {
                CoopMod.Logger.LogWarning($"[CutsceneSync] Could not create/find donkey for Gerry dig-up final state ({reason})");
                return;
            }

            if (string.IsNullOrEmpty(donkey.custom_tag))
            {
                donkey.custom_tag = DONKEY_CUSTOM_TAG;
            }

            RefreshScriptedWorldObject(donkey);
            if (cemeteryPoint != null)
            {
                donkey.OnCameToGDPoint(cemeteryPoint);
            }

            // Vanilla skull_spawn_after_dig sends this FlowCanvas event immediately after
            // spawning Donkey at the cemetery. OnCameToGDPoint only updates the GD-point
            // metadata; it does not send the event. The event queues Donkey's one-time
            // "intro" interaction, so omitting it made a remotely triggered Gerry cutscene
            // leave Donkey with only his ordinary post-intro interaction menu.
            donkey.FireEvent(DONKEY_CEMETERY_ARRIVAL_EVENT, 0f);
            CoopMod.Logger.LogInfo(
                $"[CutsceneSync] Fired Donkey cemetery arrival event for Gerry dig-up final state ({reason})");
        }

        private static void ApplyGerryDigUpDonkeyProgression(string reason)
        {
            SetPlayerParamIfLower(DONKEY_FIRST_QUEST_ID, 1f);
            StartQuestIfNeeded(DONKEY_FIRST_QUEST_ID, reason);
            GUIElements.me?.quest_list?.Redraw();
        }

        private static void RefreshScriptedWorldObject(WorldGameObject wgo)
        {
            if (wgo == null)
            {
                return;
            }

            wgo.RefreshPositionCache();
            wgo.round_and_sort?.MarkPositionDirty();
            wgo.round_and_sort?.DoUpdateStuff(true);
            wgo.RecalculateZoneBelonging();
            WGORegistry.Instance?.Register(wgo);
        }

        private static void SetPlayerParamIfLower(string paramName, float value)
        {
            var player = MainGame.me?.player;
            if (player == null || player.GetParam(paramName, 0f) >= value)
            {
                return;
            }

            player.SetParam(paramName, value);
        }

        private static void StartQuestIfNeeded(string questId, string reason)
        {
            var quests = MainGame.me?.save?.quests;
            if (quests == null || quests.IsQuestCurrent(questId) || quests.IsQuestSucced(questId))
            {
                return;
            }

            QuestDefinition quest = GameBalance.me?.GetData<QuestDefinition>(questId);
            if (quest == null)
            {
                CoopMod.Logger.LogWarning($"[CutsceneSync] Could not find quest '{questId}' for Gerry dig-up progression ({reason})");
                return;
            }

            quests.StartQuest(quest);
            CoopMod.Logger.LogInfo($"[CutsceneSync] Started quest '{questId}' for Gerry dig-up progression ({reason})");
        }

        private static void MarkQuestSucceededWithoutScripts(string questId)
        {
            var quests = MainGame.me?.save?.quests;
            if (quests == null || quests.IsQuestSucced(questId))
            {
                return;
            }

            var currentField = AccessTools.Field(typeof(QuestSystem), "_currnet_quests");
            var succeededField = AccessTools.Field(typeof(QuestSystem), "_succed_quests");
            var currentQuests = currentField?.GetValue(quests) as List<QuestState>;
            var succeededQuests = succeededField?.GetValue(quests) as List<string>;

            if (currentQuests != null)
            {
                for (int i = currentQuests.Count - 1; i >= 0; i--)
                {
                    if (currentQuests[i]?.definition?.id == questId)
                    {
                        currentQuests.RemoveAt(i);
                    }
                }
            }

            if (succeededQuests != null && !succeededQuests.Contains(questId))
            {
                succeededQuests.Add(questId);
            }
        }

        private static string GetLocalPlayerZoneId()
        {
            try
            {
                return MainGame.me?.player?.GetMyWorldZone()?.id ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static float Distance2D(Vector3 a, Vector3 b)
        {
            return Vector2.Distance(new Vector2(a.x, a.y), new Vector2(b.x, b.y));
        }

        #endregion
    }
}

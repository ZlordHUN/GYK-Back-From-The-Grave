using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Multiplayer;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Synchronizes NPC interactions (donkey intro, Gerry talk, etc.) between online coop players.
    ///
    /// Problem: FlowScript cutscenes triggered via WGO interaction don't go through
    /// GS.RunFlowScript or AttachFlowScript — they use FireEvent -> _fsc.SendEvent,
    /// which isn't hooked by <see cref="CutsceneSyncPatches"/>. Result: one player sees
    /// the donkey cutscene + walk-off + corpse, the other sees nothing. Save state
    /// diverges.
    ///
    /// Solution: Intercept <see cref="WorldGameObject.Interact"/> on NPCs and broadcast
    /// enough identity info (custom_tag + obj_id + position) for the other peer to
    /// resolve its copy. The host validates story-critical donkey interactions, then
    /// the requesting client runs the player-bound FlowScript and temporarily becomes
    /// the visual source for the donkey. Other players observe it through dialogue and
    /// visual sync without running a second copy against the wrong player context.
    /// </summary>
    [HarmonyPatch]
    public static class NpcInteractionSyncPatches
    {
        /// <summary>Guard against re-broadcasting an interaction we just received.</summary>
        private static bool isProcessingRemote = false;
        private static bool isLocalNpcInteractionActive;
        private static float localNpcInteractionStartedAt;
        private static string localNpcInteractionTag = "";
        private static string localNpcInteractionObjId = "";
        private static CSteamID observedNpcInteractionSender = CSteamID.Nil;
        private static float observedNpcInteractionStartedAt;
        private static string observedNpcInteractionTag = "";
        private static string observedNpcInteractionObjId = "";
        private static bool observedNpcInteractionParticipating;
        private static bool observedNpcInteractionMirrorsLocalExecution;
        private const float NpcInteractionActivationDistance = 350f;
        private const float PendingNpcInteractionTimeout = 20f;
        private const float DeferredFastForwardStepTimeout = 2f;
        private const float DeferredFastForwardStepDelay = 0.08f;
        private const float ClientDonkeyRequestTimeout = 8f;
        private const float NpcAnimationWindowTimeout = 120f;
        private const float CompletionSettleSeconds = 0.35f;
        private const float CompletionMaxWaitSeconds = 20f;
        private const string FirstDonkeyQuestId = "go_to_talk_with_donkey_first_time";
        private const string FirstDonkeyIntroEvent = "intro";
        private const string MetDonkeyPlayerParam = "met_donkey";
        private const string FirstCemeteryQuestId =
            "go_to_graveyard_and_talk_with_skull";
        private const string CemeteryGerryObjId = "talking_skull";
        private const string CemeteryTutorialEvent = "tutor_cemetery";
        private const string CemeteryWaitParam = "skull_wait_in_cemetery";
        private const string FirstBurialWaitParam = "waiting_for_first_bureal";
        private const string BishopObjId = "npc_bishop";
        private const string BishopIntroEvent = "intro";
        private const string MetBishopPlayerParam = "met_bishop";
        private const string ChurchLevelPlayerParam = "church_level";
        private const string GraveToolsPlayerParam =
            "take_tools_from_grave_chest";
        private const string GraveToolsQuestId =
            "take_tools_from_grave_chest";
        private const string BishopQualityTaskId = "bishop_5_qual";
        private const string KrezvoldObjId = "npc_blacksmith";
        private const string KrezvoldTaskId = "blacksmith_slimes";
        private const string KrezvoldLetterIntroEvent = "intro";
        private const string HoradricObjId = "npc_tavern owner";
        private const string HoradricLetterTaskId = "horadric_letter";
        private const string HoradricAfterSlimeEvent = "after_slime";
        private const string HoradricLetterReadyParam = "letter_to_bs";

        private static PendingRemoteNpcInteraction pendingRemoteNpcInteraction;
        private static bool clientDonkeyRequestPending;
        private static float clientDonkeyRequestSentAt;
        private static string pendingLocalCompletionTag = "";
        private static string pendingLocalCompletionObjId = "";
        private static float pendingLocalCompletionAt;
        private static float localCompletionRequestedAt;
        private static float localCompletionSettledAt;

        private sealed class PendingRemoteNpcInteraction
        {
            public CSteamID SenderID;
            public string Tag;
            public string ObjId;
            public Vector3 NpcWorldPos;
            public Vector3 JoinWorldPos;
            public bool HasJoinWorldPos;
            public string OriginZoneId;
            public bool HasScope;
            public byte Facing;
            public float ReceivedAt;
            public bool ObserveLiveDialogue;
            public readonly List<DeferredDialogueOp> BufferedDialogueOps = new List<DeferredDialogueOp>();
        }

        /// <summary>Subscribe to network events.</summary>
        public static void Enable()
        {
            clientDonkeyRequestPending = false;
            clientDonkeyRequestSentAt = 0f;
            isLocalNpcInteractionActive = false;
            localNpcInteractionStartedAt = 0f;
            localNpcInteractionTag = "";
            localNpcInteractionObjId = "";
            observedNpcInteractionSender = CSteamID.Nil;
            observedNpcInteractionStartedAt = 0f;
            observedNpcInteractionTag = "";
            observedNpcInteractionObjId = "";
            observedNpcInteractionParticipating = false;
            observedNpcInteractionMirrorsLocalExecution = false;
            pendingLocalCompletionTag = "";
            pendingLocalCompletionObjId = "";
            pendingLocalCompletionAt = 0f;
            localCompletionRequestedAt = 0f;
            localCompletionSettledAt = 0f;
            var p2p = SteamP2PManager.Instance;
            if (p2p != null)
            {
                p2p.OnNpcInteractionReceived -= OnRemoteNpcInteraction;
                p2p.OnNpcInteractionReceived += OnRemoteNpcInteraction;
                p2p.OnNpcInteractionCompleteReceived -= OnRemoteNpcInteractionComplete;
                p2p.OnNpcInteractionCompleteReceived += OnRemoteNpcInteractionComplete;
            }
            CoopMod.Logger.LogInfo("[NpcInteractionSync] Enabled");
        }

        /// <summary>Unsubscribe from network events.</summary>
        public static void Disable()
        {
            var p2p = SteamP2PManager.Instance;
            if (p2p != null)
            {
                p2p.OnNpcInteractionReceived -= OnRemoteNpcInteraction;
                p2p.OnNpcInteractionCompleteReceived -= OnRemoteNpcInteractionComplete;
            }
            isProcessingRemote = false;
            pendingRemoteNpcInteraction = null;
            clientDonkeyRequestPending = false;
            clientDonkeyRequestSentAt = 0f;
            isLocalNpcInteractionActive = false;
            localNpcInteractionStartedAt = 0f;
            localNpcInteractionTag = "";
            localNpcInteractionObjId = "";
            observedNpcInteractionSender = CSteamID.Nil;
            observedNpcInteractionStartedAt = 0f;
            observedNpcInteractionTag = "";
            observedNpcInteractionObjId = "";
            observedNpcInteractionParticipating = false;
            observedNpcInteractionMirrorsLocalExecution = false;
            pendingLocalCompletionTag = "";
            pendingLocalCompletionObjId = "";
            pendingLocalCompletionAt = 0f;
            localCompletionRequestedAt = 0f;
            localCompletionSettledAt = 0f;
            CoopMod.Logger.LogInfo("[NpcInteractionSync] Disabled");
        }

        /// <summary>
        /// Prefix on WorldGameObject.Interact. When the local player initiates an interaction
        /// with an NPC, broadcast it so the remote machine can observe the same interaction.
        /// A client-side donkey interaction is a request to the authoritative host, so
        /// the original local interaction must not consume a different story event.
        /// </summary>
        [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.Interact))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Normal)]
        public static bool Interact_Prefix(WorldGameObject __instance, WorldGameObject other_obj, bool interaction_start)
        {
            try
            {
                if (isProcessingRemote) return true;
                if (!interaction_start) return true; // Only broadcast the start of an interaction

                var onlineCoop = OnlineCoopManager.Instance;
                if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled) return true;

                if (__instance == null || __instance.obj_def == null) return true;
                if (!__instance.obj_def.IsNPC()) return true;

                // Only broadcast when the LOCAL player is the one interacting.
                // Otherwise we'd re-broadcast events triggered by the remote player proxy.
                var localPlayer = MainGame.me?.player;
                if (localPlayer == null || other_obj == null) return true;
                if (other_obj != localPlayer) return true;

                string tag = __instance.custom_tag ?? "";
                string objId = __instance.obj_id ?? "";
                Vector3 pos = __instance.transform.position;
                string zoneId = GetLocalPlayerZoneId();
                bool isDonkey = IsDonkeyIdentity(tag, objId);

                if (isDonkey)
                {
                    DonkeyCartCorpseVisualGuard.Ensure(__instance);
                    EnsureFirstDonkeyIntroEvent(__instance, "local interaction");
                }

                EnsureFirstCemeteryTutorialEvent(
                    __instance,
                    "local interaction");
                EnsureFirstBishopIntroEvent(
                    __instance,
                    "local interaction");
                EnsureKrezvoldLetterIntroEvent(
                    __instance,
                    "local interaction");
                EnsureHoradricAfterSlimeEvent(
                    __instance,
                    "local interaction");

                if (isDonkey && !onlineCoop.IsHost)
                {
                    float now = Time.realtimeSinceStartup;
                    if (clientDonkeyRequestPending &&
                        now - clientDonkeyRequestSentAt < ClientDonkeyRequestTimeout)
                    {
                        CoopMod.Logger.LogInfo("[NpcInteractionSync] Suppressed duplicate client donkey request while awaiting host");
                        return false;
                    }

                    clientDonkeyRequestPending = true;
                    clientDonkeyRequestSentAt = now;
                    CoopMod.Logger.LogInfo("[NpcInteractionSync] Client requested host approval for donkey interaction");
                    SteamP2PManager.Instance?.SendNpcInteractionToHost(
                        tag,
                        objId,
                        pos,
                        zoneId);
                    return false;
                }

                CoopMod.Logger.LogInfo($"[NpcInteractionSync] Local interact with NPC tag='{tag}', obj_id='{objId}' — broadcasting");
                SteamP2PManager.Instance?.SendNpcInteraction(tag, objId, pos, zoneId);

                localCompletionRequestedAt = 0f;
                localCompletionSettledAt = 0f;
                NpcVisualSync.Instance?.BeginLocalCutsceneActorSession(
                    __instance,
                    $"NPC interaction {objId}");
                isLocalNpcInteractionActive = true;
                localNpcInteractionStartedAt = Time.realtimeSinceStartup;
                localNpcInteractionTag = tag;
                localNpcInteractionObjId = objId;

                if (isDonkey)
                {
                    CoopMod.Logger.LogInfo("[NpcInteractionSync] Host donkey interaction - sent authoritative observation, now sending walk-to");
                    SendCutsceneWalkToLocalPlayer();
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[NpcInteractionSync] Interact prefix threw: {ex}");
            }

            return true;
        }

        /// <summary>
        /// Handle an NPC interaction broadcast by the remote player. Find the matching
        /// local WGO and replay Interact() on it with our remote-player proxy as the
        /// instigator.
        /// </summary>
        private static void OnRemoteNpcInteraction(
            CSteamID senderID,
            string tag,
            string objId,
            Vector3 expectedPos,
            string originZoneId,
            bool hasScope,
            CSteamID interactionOwner)
        {
            try
            {
                var onlineCoop = OnlineCoopManager.Instance;
                if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled) return;
                if (!onlineCoop.IsRemotePlayer(senderID)) return;

                bool isDonkey = IsDonkeyIdentity(tag, objId);

                if (isDonkey && onlineCoop.IsHost)
                {
                    if (observedNpcInteractionSender != CSteamID.Nil ||
                        HasLocalNpcVisualAuthority())
                    {
                        string requestKind =
                            observedNpcInteractionSender == senderID
                                ? "duplicate"
                                : "concurrent";
                        CoopMod.Logger.LogInfo(
                            $"[NpcInteractionSync] Ignoring {requestKind} donkey request while another player-owned flow is active");
                        return;
                    }

                    WorldGameObject donkey = ResolveNpc(tag, objId, expectedPos);
                    if (donkey == null)
                    {
                        CoopMod.Logger.LogWarning("[NpcInteractionSync] Host rejected client donkey request because the donkey could not be resolved");
                        return;
                    }

                    DonkeyCartCorpseVisualGuard.Ensure(donkey);
                    SteamP2PManager.Instance?.SendNpcInteraction(
                        donkey.custom_tag ?? tag ?? "",
                        donkey.obj_id ?? objId ?? "",
                        donkey.transform.position,
                        originZoneId ?? "",
                        senderID);
                    BeginObservedNpcAnimationWindow(senderID, tag, objId);

                    if (!ShouldActivatePendingNpcInteraction(
                            expectedPos,
                            originZoneId,
                            hasScope,
                            out string hostDeferReason))
                    {
                        pendingRemoteNpcInteraction = new PendingRemoteNpcInteraction
                        {
                            SenderID = senderID,
                            Tag = tag ?? "",
                            ObjId = objId ?? "",
                            NpcWorldPos = expectedPos,
                            JoinWorldPos = Vector3.zero,
                            HasJoinWorldPos = false,
                            OriginZoneId = originZoneId ?? "",
                            HasScope = hasScope,
                            Facing = 0,
                            ReceivedAt = Time.realtimeSinceStartup,
                            ObserveLiveDialogue = true
                        };
                        CoopMod.Logger.LogInfo(
                            $"[NpcInteractionSync] Host approved client-owned donkey flow outside local scope; observing while free ({hostDeferReason})");
                        return;
                    }

                    if (ReplayRemoteNpcInteraction(
                            senderID,
                            tag,
                            objId,
                            expectedPos,
                            true))
                    {
                        CutsceneSyncPatches.LockLocalPlayerForCutscene();
                        observedNpcInteractionParticipating = true;
                        observedNpcInteractionMirrorsLocalExecution = true;
                    }
                    else
                    {
                        CoopMod.Logger.LogWarning(
                            "[NpcInteractionSync] Host could not replay the " +
                            "approved donkey interaction; remaining a live observer");
                    }
                    return;
                }

                if (isDonkey &&
                    clientDonkeyRequestPending &&
                    senderID ==
                        (SteamLobbyManager.Instance?.GetLobbyOwner() ??
                         CSteamID.Nil) &&
                    interactionOwner == SteamUser.GetSteamID())
                {
                    clientDonkeyRequestPending = false;
                    clientDonkeyRequestSentAt = 0f;
                    CoopMod.Logger.LogInfo("[NpcInteractionSync] Host accepted donkey interaction request");
                    StartAcceptedClientDonkeyInteraction(tag, objId, expectedPos);
                    return;
                }

                BeginObservedNpcAnimationWindow(
                    interactionOwner,
                    tag,
                    objId);

                if (!ShouldActivatePendingNpcInteraction(expectedPos, originZoneId, hasScope, out string deferReason))
                {
                    pendingRemoteNpcInteraction = new PendingRemoteNpcInteraction
                    {
                        SenderID = interactionOwner,
                        Tag = tag ?? "",
                        ObjId = objId ?? "",
                        NpcWorldPos = expectedPos,
                        JoinWorldPos = Vector3.zero,
                        HasJoinWorldPos = false,
                        OriginZoneId = originZoneId ?? "",
                        HasScope = hasScope,
                        Facing = 0,
                        ReceivedAt = Time.realtimeSinceStartup,
                        ObserveLiveDialogue = true
                    };

                    string senderName = SteamFriends.GetFriendPersonaName(senderID);
                    CoopMod.Logger.LogInfo($"[NpcInteractionSync] Remote player '{senderName}' interacted with NPC '{GetPendingNpcLabel(pendingRemoteNpcInteraction)}' outside local scope — deferring ({deferReason})");
                    return;
                }

                if (!ReplayRemoteNpcInteraction(
                        interactionOwner,
                        tag,
                        objId,
                        expectedPos,
                        isDonkey))
                {
                    observedNpcInteractionSender = CSteamID.Nil;
                    observedNpcInteractionStartedAt = 0f;
                    observedNpcInteractionTag = "";
                    observedNpcInteractionObjId = "";
                    observedNpcInteractionParticipating = false;
                    observedNpcInteractionMirrorsLocalExecution = false;
                    return;
                }

                // The actual letterbox transition is detected and promoted by
                // CutsceneSyncPatches. Start as ordinary observed dialogue here;
                // cinematic interactions will upgrade generically when their
                // FlowScript shows the bars.
                Patches.CutsceneSyncPatches.LockLocalPlayerForObservedDialogue();
                observedNpcInteractionParticipating = true;
                observedNpcInteractionMirrorsLocalExecution = true;
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[NpcInteractionSync] Error handling remote NPC interaction: {ex}");
            }
        }

        private static bool StartAcceptedClientDonkeyInteraction(
            string tag,
            string objId,
            Vector3 expectedPos)
        {
            WorldGameObject donkey = ResolveNpc(tag, objId, expectedPos);
            WorldGameObject localPlayer = MainGame.me?.player;
            if (donkey == null || localPlayer == null)
            {
                CoopMod.Logger.LogWarning(
                    "[NpcInteractionSync] Cannot start approved donkey interaction on client - donkey/player unavailable");
                return false;
            }

            DonkeyCartCorpseVisualGuard.Ensure(donkey);
            EnsureFirstDonkeyIntroEvent(donkey, "host-approved client interaction");
            localCompletionRequestedAt = 0f;
            localCompletionSettledAt = 0f;
            NpcVisualSync.Instance?.BeginLocalCutsceneActorSession(
                donkey,
                "host-approved donkey interaction");
            isLocalNpcInteractionActive = true;
            localNpcInteractionStartedAt = Time.realtimeSinceStartup;
            localNpcInteractionTag = tag ?? "";
            localNpcInteractionObjId = objId ?? "";

            try
            {
                isProcessingRemote = true;
                CoopMod.Logger.LogInfo(
                    "[NpcInteractionSync] Client starting host-approved donkey FlowScript with the requesting player context");
                DialogueSync.Instance?.NotifyDialogueStart(
                    string.IsNullOrEmpty(objId) ? "donkey" : objId);
                SendCutsceneWalkToLocalPlayer();
                donkey.Interact(localPlayer, true);
                return true;
            }
            catch (System.Exception ex)
            {
                isLocalNpcInteractionActive = false;
                localNpcInteractionStartedAt = 0f;
                localNpcInteractionTag = "";
                localNpcInteractionObjId = "";
                CoopMod.Logger.LogError(
                    $"[NpcInteractionSync] Approved client donkey interaction failed: {ex}");
                return false;
            }
            finally
            {
                isProcessingRemote = false;
            }
        }

        /// <summary>
        /// Repair saves produced before remote Gerry finalization replayed Donkey's
        /// on_came_to_cemetery event. Those saves have the first-Donkey quest but no queued
        /// intro interaction, so vanilla falls through to the later oil/leave menu.
        /// </summary>
        private static void EnsureFirstDonkeyIntroEvent(
            WorldGameObject donkey,
            string source)
        {
            var save = MainGame.me?.save;
            var player = MainGame.me?.player;
            if (donkey == null || save?.quests == null || player == null ||
                !save.quests.IsQuestCurrent(FirstDonkeyQuestId) ||
                player.GetParam(MetDonkeyPlayerParam, 0f) >= 1f)
            {
                return;
            }

            if (donkey.custom_interaction_events == null)
            {
                donkey.custom_interaction_events = new List<string>();
            }

            int introIndex = donkey.custom_interaction_events.IndexOf(
                FirstDonkeyIntroEvent);
            if (introIndex == 0)
            {
                return;
            }

            string previousEvents = donkey.custom_interaction_events.Count == 0
                ? "none"
                : string.Join(",", donkey.custom_interaction_events.ToArray());
            if (introIndex > 0)
            {
                donkey.custom_interaction_events.RemoveAt(introIndex);
            }
            donkey.custom_interaction_events.Insert(0, FirstDonkeyIntroEvent);

            CoopMod.Logger.LogWarning(
                $"[NpcInteractionSync] Restored missing first-Donkey intro event " +
                $"({source}; previous events={previousEvents})");
        }

        /// <summary>
        /// Repair saves where a client-owned autopsy flow placed cemetery Gerry but
        /// the one-shot interaction event was not retained by the host save. The
        /// active quest and its vanilla player flags make this recovery unambiguous.
        /// </summary>
        private static void EnsureFirstCemeteryTutorialEvent(
            WorldGameObject npc,
            string source)
        {
            QuestSystem quests = MainGame.me?.save?.quests;
            WorldGameObject player = MainGame.me?.player;
            if (npc == null || player == null || quests == null ||
                !string.Equals(
                    npc.obj_id,
                    CemeteryGerryObjId,
                    System.StringComparison.Ordinal) ||
                !quests.IsQuestCurrent(FirstCemeteryQuestId) ||
                player.GetParam(CemeteryWaitParam, 0f) < 1f ||
                player.GetParam(FirstBurialWaitParam, 0f) >= 1f)
            {
                return;
            }

            if (npc.custom_interaction_events == null)
                npc.custom_interaction_events = new List<string>();

            int eventIndex = npc.custom_interaction_events.IndexOf(
                CemeteryTutorialEvent);
            if (eventIndex == 0)
                return;

            string previousEvents = npc.custom_interaction_events.Count == 0
                ? "none"
                : string.Join(",", npc.custom_interaction_events.ToArray());
            if (eventIndex > 0)
                npc.custom_interaction_events.RemoveAt(eventIndex);
            npc.custom_interaction_events.Insert(0, CemeteryTutorialEvent);
            npc.RedrawBubble(null);

            SpawnSync.Instance?.NotifyInteractionEventsChanged(npc);
            CoopMod.Logger.LogWarning(
                $"[NpcInteractionSync] Restored missing cemetery tutorial event " +
                $"({source}; previous events={previousEvents})");
        }

        /// <summary>
        /// Bishop's arrival normally queues the one-shot "intro" interaction from
        /// on_came_to_finish. A client-owned arrival can lose that transient queue
        /// while the Bishop still reaches the graveyard, causing the first talk to
        /// fall through to the ordinary answer menu. The intro writes met/church
        /// markers and starts the grave-tools quest. Its dialogue flag is later
        /// cleared when the trunk opens, so either an active or succeeded quest
        /// also proves that the cinematic conversation already ran.
        /// </summary>
        private static void EnsureFirstBishopIntroEvent(
            WorldGameObject npc,
            string source)
        {
            WorldGameObject player = MainGame.me?.player;
            if (npc == null || player == null ||
                !string.Equals(
                    npc.obj_id,
                    BishopObjId,
                    System.StringComparison.Ordinal))
            {
                return;
            }

            if (HasCompletedFirstBishopIntro())
                return;

            if (npc.custom_interaction_events == null)
                npc.custom_interaction_events = new List<string>();

            int introIndex = npc.custom_interaction_events.IndexOf(
                BishopIntroEvent);
            if (introIndex == 0)
                return;

            string previousEvents = npc.custom_interaction_events.Count == 0
                ? "none"
                : string.Join(",", npc.custom_interaction_events.ToArray());
            if (introIndex > 0)
                npc.custom_interaction_events.RemoveAt(introIndex);
            npc.custom_interaction_events.Insert(0, BishopIntroEvent);
            npc.RedrawBubble(null);

            SpawnSync.Instance?.NotifyInteractionEventsChanged(npc);
            CoopMod.Logger.LogWarning(
                $"[NpcInteractionSync] Restored missing first-Bishop intro event " +
                $"({source}; previous events={previousEvents}, " +
                $"met={player.GetParam(MetBishopPlayerParam, 0f):F0}, " +
                $"church={player.GetParam(ChurchLevelPlayerParam, 0f):F0}, " +
                $"tools={player.GetParam(GraveToolsPlayerParam, 0f):F0})");
        }

        /// <summary>
        /// Durable proof that the one-shot first Bishop conversation completed.
        /// The departure repair uses the same proof as the interaction repair so
        /// it can never remove the Bishop before the player receives the prologue
        /// progression written by that conversation.
        /// </summary>
        internal static bool HasCompletedFirstBishopIntro()
        {
            WorldGameObject player = MainGame.me?.player;
            GameSave save = MainGame.me?.save;
            if (player == null || save == null)
                return false;

            QuestSystem quests = save.quests;
            bool graveToolsStepReached =
                player.GetParam(GraveToolsPlayerParam, 0f) >= 1f ||
                quests?.IsQuestCurrent(GraveToolsQuestId) == true ||
                quests?.IsQuestSucced(GraveToolsQuestId) == true;
            string bishopNpcId =
                StoryCatalogRuntime.ResolveKnownNpcId(BishopObjId);
            KnownNPC bishop = save.known_npcs?.GetNPC(bishopNpcId);
            KnownNPC.TaskState.State bishopTaskState = bishop == null
                ? KnownNPC.TaskState.State.Unknown
                : bishop.GetQuestState(BishopQualityTaskId);
            bool bishopTaskReached =
                bishopTaskState == KnownNPC.TaskState.State.Visible ||
                bishopTaskState == KnownNPC.TaskState.State.Complete;
            bool introFlagsReached =
                player.GetParam(MetBishopPlayerParam, 0f) >= 1f &&
                player.GetParam(ChurchLevelPlayerParam, 0f) >= 1f;
            return graveToolsStepReached || bishopTaskReached || introFlagsReached;
        }

        /// <summary>
        /// Horadric queues Krezvold's one-shot intro when he hands over the letter.
        /// Restore only that event when the durable task and WGO state prove the
        /// letter handoff happened but Krezvold's tutorial has not started.
        /// </summary>
        private static void EnsureKrezvoldLetterIntroEvent(
            WorldGameObject npc,
            string source)
        {
            if (!IsNpcIdentityMatch(npc, KrezvoldObjId, KrezvoldObjId))
                return;

            GameSave save = MainGame.me?.save;
            KnownNPCList knownNpcs = save?.known_npcs;
            KnownNPC horadric = knownNpcs?.GetNPC(
                StoryCatalogRuntime.ResolveKnownNpcId(HoradricObjId));
            KnownNPC krezvold = knownNpcs?.GetNPC(
                StoryCatalogRuntime.ResolveKnownNpcId(KrezvoldObjId));
            if (horadric == null || krezvold == null ||
                horadric.GetQuestState(HoradricLetterTaskId) !=
                    KnownNPC.TaskState.State.Visible ||
                krezvold.GetQuestState(KrezvoldTaskId) !=
                    KnownNPC.TaskState.State.Unknown)
            {
                return;
            }

            WorldGameObject horadricWgo = WorldMap.GetWorldGameObjectByObjId(
                HoradricObjId,
                true);
            if (horadricWgo?.data == null ||
                horadricWgo.GetParam(HoradricLetterReadyParam, 0f) < 1f)
            {
                return;
            }

            RestoreInteractionEventFirst(
                npc,
                KrezvoldLetterIntroEvent,
                "Krezvold letter intro",
                source);
        }

        /// <summary>
        /// Completing Krezvold's slime task queues Horadric's after_slime event.
        /// A lost transient event otherwise leaves Horadric's task incomplete and
        /// prevents the beer handoff even though the tutorial is already finished.
        /// </summary>
        private static void EnsureHoradricAfterSlimeEvent(
            WorldGameObject npc,
            string source)
        {
            if (!IsNpcIdentityMatch(npc, HoradricObjId, HoradricObjId))
                return;

            KnownNPCList knownNpcs = MainGame.me?.save?.known_npcs;
            KnownNPC krezvold = knownNpcs?.GetNPC(
                StoryCatalogRuntime.ResolveKnownNpcId(KrezvoldObjId));
            KnownNPC horadric = knownNpcs?.GetNPC(
                StoryCatalogRuntime.ResolveKnownNpcId(HoradricObjId));
            if (krezvold == null || horadric == null ||
                krezvold.GetQuestState(KrezvoldTaskId) !=
                    KnownNPC.TaskState.State.Complete ||
                horadric.GetQuestState(HoradricLetterTaskId) ==
                    KnownNPC.TaskState.State.Complete)
            {
                return;
            }

            RestoreInteractionEventFirst(
                npc,
                HoradricAfterSlimeEvent,
                "Horadric post-slime",
                source);
        }

        private static void RestoreInteractionEventFirst(
            WorldGameObject npc,
            string eventName,
            string description,
            string source)
        {
            if (npc.custom_interaction_events == null)
                npc.custom_interaction_events = new List<string>();

            int eventIndex = npc.custom_interaction_events.IndexOf(eventName);
            if (eventIndex == 0)
                return;

            string previousEvents = npc.custom_interaction_events.Count == 0
                ? "none"
                : string.Join(",", npc.custom_interaction_events.ToArray());
            if (eventIndex > 0)
                npc.custom_interaction_events.RemoveAt(eventIndex);
            npc.custom_interaction_events.Insert(0, eventName);
            npc.RedrawBubble(null);

            SpawnSync.Instance?.NotifyInteractionEventsChanged(npc);
            CoopMod.Logger.LogWarning(
                $"[NpcInteractionSync] Restored missing {description} event " +
                $"({source}; previous events={previousEvents})");
        }

        private static bool ReplayRemoteNpcInteraction(CSteamID senderID, string tag, string objId, Vector3 expectedPos, bool isDonkey)
        {
            bool replayStarted = false;
            try
            {
                var onlineCoop = OnlineCoopManager.Instance;
                if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled) return false;

                WorldGameObject npc = ResolveNpc(tag, objId, expectedPos);
                if (npc == null)
                {
                    CoopMod.Logger.LogWarning($"[NpcInteractionSync] Could not resolve NPC for tag='{tag}', obj_id='{objId}', pos={expectedPos} — interaction dropped");
                    return false;
                }

                if (isDonkey)
                {
                    DonkeyCartCorpseVisualGuard.Ensure(npc);
                }

                WorldGameObject remotePlayerWgo =
                    onlineCoop.GetRemotePlayer(senderID);
                if (remotePlayerWgo == null)
                {
                    CoopMod.Logger.LogWarning("[NpcInteractionSync] Remote player WGO is null — cannot replay interaction");
                    return false;
                }

                CoopMod.Logger.LogInfo($"[NpcInteractionSync] Observing remote NPC interaction with '{npc.obj_id}' (tag='{npc.custom_tag}') — live-shared via dialogue/visual sync, not replayed");

                replayStarted = true;
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[NpcInteractionSync] Error replaying remote NPC interaction: {ex}");
            }

            return replayStarted;
        }

        public static bool TryDeferCutsceneWalk(CSteamID senderID, Vector3 joinWorldPos, byte facing)
        {
            var pending = pendingRemoteNpcInteraction;
            if (pending == null || pending.SenderID != senderID)
            {
                return false;
            }

            pending.JoinWorldPos = joinWorldPos;
            pending.HasJoinWorldPos = true;
            pending.HasScope = true;
            pending.Facing = facing;
            CoopMod.Logger.LogInfo($"[NpcInteractionSync] Deferring CutsceneWalkTo for pending NPC interaction '{GetPendingNpcLabel(pending)}'");
            return true;
        }

        public static bool TryBufferRemoteDialogueAdvance(CSteamID senderID)
        {
            var pending = pendingRemoteNpcInteraction;
            if (pending == null || pending.SenderID != senderID)
            {
                return false;
            }

            if (pending.ObserveLiveDialogue)
            {
                return false;
            }

            pending.BufferedDialogueOps.Add(new DeferredDialogueOp { Type = DeferredDialogueOpType.Advance });
            CoopMod.Logger.LogInfo($"[NpcInteractionSync] Buffered dialogue advance for deferred NPC interaction '{GetPendingNpcLabel(pending)}' (ops={pending.BufferedDialogueOps.Count})");
            return true;
        }

        public static bool TryBufferRemoteDialogueChoice(CSteamID senderID, int choiceIndex, string choiceText)
        {
            var pending = pendingRemoteNpcInteraction;
            if (pending == null || pending.SenderID != senderID)
            {
                return false;
            }

            if (pending.ObserveLiveDialogue)
            {
                return false;
            }

            pending.BufferedDialogueOps.Add(new DeferredDialogueOp
            {
                Type = DeferredDialogueOpType.Choice,
                ChoiceIndex = choiceIndex,
                ChoiceText = choiceText
            });
            CoopMod.Logger.LogInfo($"[NpcInteractionSync] Buffered dialogue choice {choiceIndex} for deferred NPC interaction '{GetPendingNpcLabel(pending)}' (ops={pending.BufferedDialogueOps.Count})");
            return true;
        }

        public static bool TryEndDeferredRemoteNpcInteraction(CSteamID senderID)
        {
            var pending = pendingRemoteNpcInteraction;
            if (pending == null || pending.SenderID != senderID)
            {
                return false;
            }

            pendingRemoteNpcInteraction = null;
            CoopMod.Logger.LogInfo(
                $"[NpcInteractionSync] Remote dialogue ended while NPC interaction " +
                $"'{GetPendingNpcLabel(pending)}' was out of scope; discarded deferred cinematic");
            return true;
        }

        internal static bool IsAwaitingLocalParticipation()
        {
            return pendingRemoteNpcInteraction != null;
        }

        internal static bool ShouldSuppressPendingDialogueStart(
            WorldGameObject target)
        {
            return clientDonkeyRequestPending &&
                   target != null &&
                   IsDonkeyIdentity(target.custom_tag, target.obj_id);
        }

        private static void BeginObservedNpcAnimationWindow(
            CSteamID senderID,
            string tag,
            string objId)
        {
            observedNpcInteractionSender = senderID;
            observedNpcInteractionStartedAt = Time.realtimeSinceStartup;
            observedNpcInteractionTag = tag ?? "";
            observedNpcInteractionObjId = objId ?? "";
            observedNpcInteractionParticipating = false;
            observedNpcInteractionMirrorsLocalExecution = false;
        }

        internal static void NotifyObservedNpcInteractionEnded(CSteamID senderID)
        {
            if (observedNpcInteractionSender == CSteamID.Nil ||
                observedNpcInteractionSender != senderID)
            {
                return;
            }

            // Interaction FlowScripts can continue moving actors after their final
            // dialogue bubble. A machine that replayed the interaction keeps its
            // smooth local simulation; an observer that never replayed it continues
            // accepting the initiator's puppet stream until completion.
        }

        internal static bool IsObservedNpcInteractionParticipating(
            CSteamID senderID)
        {
            return senderID != CSteamID.Nil &&
                   observedNpcInteractionSender == senderID &&
                   observedNpcInteractionParticipating;
        }

        internal static void NotifyLocalNpcInteractionDialogueEnded()
        {
            if (!isLocalNpcInteractionActive ||
                MainGame.me?.player_char?.control_enabled != true)
            {
                return;
            }

            RequestLocalInteractionCompletion(
                "dialogue ended with player control already enabled");
        }

        private static void OnRemoteNpcInteractionComplete(
            CSteamID senderID,
            string tag,
            string objId)
        {
            if (observedNpcInteractionSender == CSteamID.Nil ||
                observedNpcInteractionSender != senderID)
            {
                return;
            }
            if (!IsNpcIdentityMatch(
                    tag,
                    objId,
                    observedNpcInteractionTag,
                    observedNpcInteractionObjId))
            {
                return;
            }

            bool wasParticipating = observedNpcInteractionParticipating;
            observedNpcInteractionSender = CSteamID.Nil;
            observedNpcInteractionStartedAt = 0f;
            observedNpcInteractionTag = "";
            observedNpcInteractionObjId = "";
            observedNpcInteractionParticipating = false;
            observedNpcInteractionMirrorsLocalExecution = false;

            if (pendingRemoteNpcInteraction != null &&
                pendingRemoteNpcInteraction.SenderID == senderID &&
                IsNpcIdentityMatch(
                    tag,
                    objId,
                    pendingRemoteNpcInteraction.Tag,
                    pendingRemoteNpcInteraction.ObjId))
            {
                pendingRemoteNpcInteraction = null;
            }

            // NPC interactions and live FlowScripts have independent lifetimes. The
            // donkey interaction can finish after its initiator has already started
            // the morgue flow; releasing its presentation lock here would tear down
            // the newer flow's letterbox, HUD state, and follow target.
            bool liveCutsceneStillOwnsPresentation =
                CutsceneSyncPatches.ShouldKeepLocalPlayerLocked(senderID);
            if (!liveCutsceneStillOwnsPresentation)
            {
                OnlineCoopManager.Instance?.EndObservedCutsceneFollow(
                    "remote NPC interaction completed");
                if (wasParticipating)
                {
                    CutsceneSyncPatches.UnlockLocalPlayerAfterCutscene();
                }
            }
            else
            {
                CoopMod.Logger.LogInfo(
                    "[NpcInteractionSync] NPC interaction completed while a live " +
                    "remote FlowScript still owns presentation; preserving cutscene lock and follow");
            }
            CoopMod.Logger.LogInfo(
                $"[NpcInteractionSync] Remote NPC interaction " +
                $"'{objId}' completed; returned NPC visual authority");
        }

        internal static bool IsNpcNetworkPuppetWindowActive()
        {
            if (observedNpcInteractionSender == CSteamID.Nil ||
                observedNpcInteractionMirrorsLocalExecution)
                return false;

            if (Time.realtimeSinceStartup - observedNpcInteractionStartedAt <=
                NpcAnimationWindowTimeout)
            {
                return true;
            }

            CoopMod.Logger.LogWarning(
                "[NpcInteractionSync] Observed NPC animation window timed out");
            observedNpcInteractionSender = CSteamID.Nil;
            observedNpcInteractionStartedAt = 0f;
            observedNpcInteractionTag = "";
            observedNpcInteractionObjId = "";
            observedNpcInteractionParticipating = false;
            observedNpcInteractionMirrorsLocalExecution = false;
            return false;
        }

        internal static bool HasRemoteNpcVisualAuthority()
        {
            return observedNpcInteractionSender != CSteamID.Nil &&
                   IsNpcNetworkPuppetWindowActive();
        }

        internal static bool IsRemoteNpcVisualAuthority(CSteamID senderID)
        {
            return HasRemoteNpcVisualAuthority() &&
                   observedNpcInteractionSender == senderID;
        }

        internal static bool HasLocalNpcVisualAuthority()
        {
            return isLocalNpcInteractionActive;
        }

        internal static bool IsLocalCutsceneNpcActor(WorldGameObject wgo)
        {
            return HasLocalNpcVisualAuthority() &&
                   wgo != null &&
                   IsNpcIdentityMatch(
                       wgo,
                       localNpcInteractionTag,
                       localNpcInteractionObjId);
        }

        internal static bool TryGetLocalDonkeyVisualActor(
            out WorldGameObject donkey)
        {
            donkey = null;
            if (!HasLocalNpcVisualAuthority() ||
                !IsDonkeyIdentity(
                    localNpcInteractionTag,
                    localNpcInteractionObjId))
            {
                return false;
            }

            donkey = ResolveNpc(
                localNpcInteractionTag,
                localNpcInteractionObjId,
                Vector3.zero);
            return DonkeyCartCorpseVisualGuard.IsDonkey(donkey);
        }

        internal static bool HasActiveMirroredLocalInteraction()
        {
            return observedNpcInteractionSender != CSteamID.Nil &&
                   observedNpcInteractionMirrorsLocalExecution;
        }

        internal static bool IsAuthoritativeNpcAnimationWindowActive()
        {
            if (!isLocalNpcInteractionActive)
                return false;

            if (Time.realtimeSinceStartup - localNpcInteractionStartedAt <=
                NpcAnimationWindowTimeout)
            {
                return true;
            }

            isLocalNpcInteractionActive = false;
            localNpcInteractionStartedAt = 0f;
            localNpcInteractionTag = "";
            localNpcInteractionObjId = "";
            return false;
        }

        internal static bool ShouldBlockLocalDialogueInput()
        {
            return pendingRemoteNpcInteraction != null;
        }

        internal static bool IsRemoteNpcInteractionDeferred(
            CSteamID senderID)
        {
            return pendingRemoteNpcInteraction != null &&
                   pendingRemoteNpcInteraction.SenderID == senderID;
        }

        /// <summary>
        /// Resolve a WorldGameObject from the identity hints we broadcast. Prefers
        /// custom_tag (unique for story NPCs like donkey), falls back to obj_id +
        /// nearest-to-position match for generic NPCs.
        /// </summary>
        private static WorldGameObject ResolveNpc(string tag, string objId, Vector3 expectedPos)
        {
            // 1. Custom tag lookup (preferred for story NPCs)
            if (!string.IsNullOrEmpty(tag))
            {
                try
                {
                    var byTag = WorldMap.GetWorldGameObjectByCustomTag(tag, true);
                    if (byTag != null) return byTag;
                }
                catch { /* fallthrough */ }
            }

            // 2. obj_id lookup (unique singletons like "donkey")
            if (!string.IsNullOrEmpty(objId))
            {
                try
                {
                    var byId = WorldMap.GetWorldGameObjectByObjId(objId, true);
                    if (byId != null) return byId;
                }
                catch { /* fallthrough */ }

                // 3. Nearest-match: if there are multiple WGOs with this obj_id, pick
                //    the one closest to the position the sender had.
                WorldGameObject best = null;
                float bestDist = float.MaxValue;
                var all = WorldMap.GetWorldGameObjectsByObjId(objId);
                if (all != null)
                {
                    foreach (var wgo in all)
                    {
                        if (wgo == null) continue;
                        float d = (wgo.transform.position - expectedPos).sqrMagnitude;
                        if (d < bestDist)
                        {
                            bestDist = d;
                            best = wgo;
                        }
                    }
                }
                return best;
            }

            return null;
        }

        private static bool IsNpcIdentityMatch(
            WorldGameObject wgo,
            string tag,
            string objId)
        {
            if (wgo == null)
                return false;

            if (!string.IsNullOrEmpty(tag) &&
                string.Equals(
                    wgo.custom_tag,
                    tag,
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !string.IsNullOrEmpty(objId) &&
                   string.Equals(
                       wgo.obj_id,
                       objId,
                       System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNpcIdentityMatch(
            string firstTag,
            string firstObjId,
            string secondTag,
            string secondObjId)
        {
            if (!string.IsNullOrEmpty(firstTag) &&
                !string.IsNullOrEmpty(secondTag) &&
                string.Equals(
                    firstTag,
                    secondTag,
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !string.IsNullOrEmpty(firstObjId) &&
                   !string.IsNullOrEmpty(secondObjId) &&
                   string.Equals(
                       firstObjId,
                       secondObjId,
                       System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDonkeyIdentity(string tag, string objId)
        {
            return string.Equals(tag, "donkey", System.StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(objId, "donkey", System.StringComparison.OrdinalIgnoreCase);
        }

        private static void TickPendingRemoteNpcInteraction()
        {
            var pending = pendingRemoteNpcInteraction;
            if (pending == null)
            {
                return;
            }

            float age = Time.realtimeSinceStartup - pending.ReceivedAt;
            if (age > PendingNpcInteractionTimeout)
            {
                CoopMod.Logger.LogInfo(
                    $"[NpcInteractionSync] Deferred NPC interaction " +
                    $"'{GetPendingNpcLabel(pending)}' expired after {age:F1}s without local participation");
                pendingRemoteNpcInteraction = null;
                DialogueSync.Instance?.ClearPendingRemoteOptions(
                    pending.SenderID);
                return;
            }

            Vector3 activationPos = GetPendingActivationPosition(pending);
            if (ShouldActivatePendingNpcInteraction(activationPos, pending.OriginZoneId, pending.HasScope, out string reason))
            {
                ActivatePendingRemoteNpcInteraction(reason);
            }
        }

        private static void ActivatePendingRemoteNpcInteraction(string activationReason)
        {
            var pending = pendingRemoteNpcInteraction;
            if (pending == null)
            {
                return;
            }

            pendingRemoteNpcInteraction = null;

            var bufferedOps = new List<DeferredDialogueOp>(pending.BufferedDialogueOps);
            string label = GetPendingNpcLabel(pending);
            CoopMod.Logger.LogInfo($"[NpcInteractionSync] Activating deferred NPC interaction '{label}' ({activationReason}), bufferedOps={bufferedOps.Count}");

            BeginObservedNpcAnimationWindow(
                pending.SenderID,
                pending.Tag,
                pending.ObjId);

            CutsceneSyncPatches.LockLocalPlayerForCutscene();
            observedNpcInteractionParticipating = true;
            observedNpcInteractionMirrorsLocalExecution = false;
            DialogueSync.Instance?.ShowPendingRemoteDialogueOptions(
                pending.SenderID);
            if (pending.HasJoinWorldPos)
            {
                OnlineCoopManager.Instance?.StartDeferredCutsceneWalk(pending.SenderID, pending.JoinWorldPos, pending.Facing);
            }

            // Buffered dialogue ops are not fast-forwarded: this is a live join, not
            // a second FlowScript execution, so there is no local dialogue UI. Dialogue
            // was already synced live via DialogueSync (speech bubbles).
        }

        private static IEnumerator FastForwardBufferedDialogue(List<DeferredDialogueOp> bufferedOps, string npcLabel)
        {
            yield return null;

            for (int i = 0; i < bufferedOps.Count; i++)
            {
                var op = bufferedOps[i];
                float deadline = Time.realtimeSinceStartup + DeferredFastForwardStepTimeout;
                while (!HasActiveDialogueUI() && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                if (op.Type == DeferredDialogueOpType.Choice)
                {
                    DialogueSync.Instance?.ApplyRemoteDialogueChoiceNow(op.ChoiceIndex);
                    CoopMod.Logger.LogInfo($"[NpcInteractionSync] Fast-forwarded deferred choice {op.ChoiceIndex} for '{npcLabel}' ({i + 1}/{bufferedOps.Count})");
                }
                else
                {
                    DialogueSync.Instance?.ApplyRemoteDialogueAdvanceNow();
                    CoopMod.Logger.LogInfo($"[NpcInteractionSync] Fast-forwarded deferred dialogue advance for '{npcLabel}' ({i + 1}/{bufferedOps.Count})");
                }

                yield return new WaitForSeconds(DeferredFastForwardStepDelay);
            }
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

        private static bool ShouldActivatePendingNpcInteraction(Vector3 originWorldPos, string originZoneId, bool hasScope, out string reason)
        {
            reason = "";

            if (!hasScope || (originWorldPos == Vector3.zero && string.IsNullOrEmpty(originZoneId)))
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
            if (distance > NpcInteractionActivationDistance)
            {
                reason = $"distance {distance:F1} > {NpcInteractionActivationDistance:F1}";
                return false;
            }

            reason = $"in scope distance={distance:F1}, zone='{localZoneId}'";
            return true;
        }

        private static Vector3 GetPendingActivationPosition(PendingRemoteNpcInteraction pending)
        {
            if (pending.HasJoinWorldPos)
            {
                return pending.JoinWorldPos;
            }

            return pending.NpcWorldPos;
        }

        private static string GetPendingNpcLabel(PendingRemoteNpcInteraction pending)
        {
            if (pending == null)
            {
                return "";
            }

            if (!string.IsNullOrEmpty(pending.Tag))
            {
                return pending.Tag;
            }

            return pending.ObjId ?? "";
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

        private static void SendCutsceneWalkToLocalPlayer()
        {
            try
            {
                var localPlayer = MainGame.me?.player;
                var character = MainGame.me?.player_char;
                if (localPlayer == null || character == null)
                {
                    CoopMod.Logger.LogWarning("[NpcInteractionSync] Cannot send donkey CutsceneWalkTo - local player/facing unavailable");
                    return;
                }

                SteamP2PManager.Instance?.SendCutsceneWalkTo(localPlayer.transform.position, (byte)character.anim_direction);
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning($"[NpcInteractionSync] Failed to send donkey CutsceneWalkTo: {ex.Message}");
            }
        }

        public static void TickRemoteDonkeyStateSync()
        {
            if (clientDonkeyRequestPending &&
                Time.realtimeSinceStartup - clientDonkeyRequestSentAt >=
                ClientDonkeyRequestTimeout)
            {
                clientDonkeyRequestPending = false;
                clientDonkeyRequestSentAt = 0f;
                CoopMod.Logger.LogWarning(
                    "[NpcInteractionSync] Donkey interaction approval timed out; local interaction may be retried");
            }

            TickPendingLocalInteractionCompletion();

            if (pendingLocalCompletionAt > 0f &&
                Time.realtimeSinceStartup >= pendingLocalCompletionAt)
            {
                string tag = pendingLocalCompletionTag;
                string objId = pendingLocalCompletionObjId;
                pendingLocalCompletionTag = "";
                pendingLocalCompletionObjId = "";
                pendingLocalCompletionAt = 0f;
                SteamP2PManager.Instance?.SendNpcInteractionComplete(
                    tag,
                    objId);
            }

            TickPendingRemoteNpcInteraction();
        }

        public static bool ShouldBypassCutsceneWalkScopeCheck()
        {
            return false;
        }

        private static void RequestLocalInteractionCompletion(
            string reason)
        {
            if (!isLocalNpcInteractionActive ||
                localCompletionRequestedAt > 0f)
                return;

            localCompletionRequestedAt = Time.realtimeSinceStartup;
            localCompletionSettledAt = 0f;
            CoopMod.Logger.LogInfo(
                $"[NpcInteractionSync] NPC interaction " +
                $"'{localNpcInteractionObjId}' requested completion ({reason}); " +
                "waiting for enrolled actors to settle");
        }

        private static void TickPendingLocalInteractionCompletion()
        {
            if (!isLocalNpcInteractionActive ||
                localCompletionRequestedAt <= 0f)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            float waitingFor = now - localCompletionRequestedAt;
            bool moving =
                NpcVisualSync.Instance?.AreLocalCutsceneActorsMoving() == true;

            if (waitingFor < CompletionSettleSeconds || moving)
            {
                localCompletionSettledAt = 0f;
                if (waitingFor < CompletionMaxWaitSeconds)
                    return;
            }
            else if (localCompletionSettledAt <= 0f)
            {
                localCompletionSettledAt = now;
                return;
            }
            else if (now - localCompletionSettledAt <
                     CompletionSettleSeconds)
            {
                return;
            }

            FinalizeLocalInteractionCompletion(
                waitingFor >= CompletionMaxWaitSeconds
                    ? "actor settle timeout"
                    : "actors settled");
        }

        private static void FinalizeLocalInteractionCompletion(
            string reason)
        {
            // NpcVisualSync needs one update to publish the epoch-closing final
            // pose before observers discard this initiator as their visual source.
            pendingLocalCompletionTag = localNpcInteractionTag;
            pendingLocalCompletionObjId = localNpcInteractionObjId;
            pendingLocalCompletionAt =
                Time.realtimeSinceStartup + 0.20f;

            CoopMod.Logger.LogInfo(
                $"[NpcInteractionSync] NPC interaction " +
                $"'{localNpcInteractionObjId}' ending ({reason}); " +
                "final visual pose will precede completion");

            isLocalNpcInteractionActive = false;
            localNpcInteractionStartedAt = 0f;
            localNpcInteractionTag = "";
            localNpcInteractionObjId = "";
            localCompletionRequestedAt = 0f;
            localCompletionSettledAt = 0f;
        }

        [HarmonyPatch(typeof(GS), nameof(GS.SetPlayerEnable))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static bool GS_SetPlayerEnable_Prefix(bool player_enabled, bool affect_cinematic)
        {
            if (player_enabled)
            {
                RequestLocalInteractionCompletion(
                    "player control restored");
            }

            return true;
        }
    }
}

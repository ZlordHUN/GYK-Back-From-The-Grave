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
    /// resolve its copy. Story-critical donkey interactions are host-authoritative:
    /// clients request the interaction, the host runs the one canonical FlowScript,
    /// and clients observe it through dialogue/visual sync.
    /// </summary>
    [HarmonyPatch]
    public static class NpcInteractionSyncPatches
    {
        /// <summary>Guard against re-broadcasting an interaction we just received.</summary>
        private static bool isProcessingRemote = false;
        private static bool isHeadlessDonkeyFlowActive = false;
        private static bool isRemoteRequestedDonkeyFlowActive = false;
        private static bool remoteDonkeySpeedStored = false;
        private static float remoteDonkeyOriginalSpeed;
        private static float headlessDonkeyFlowStartedAt;
        private static Vector3 headlessDonkeyOriginWorldPos;
        private static string headlessDonkeyOriginZoneId = "";
        private static bool headlessDonkeyHasScope;
        private const float HeadlessDonkeyFlowTimeout = 45f;
        private const float NpcInteractionActivationDistance = 350f;
        private const float PendingNpcInteractionTimeout = 20f;
        private const float DeferredFastForwardStepTimeout = 2f;
        private const float DeferredFastForwardStepDelay = 0.08f;
        private const float ClientDonkeyRequestTimeout = 8f;

        private static PendingRemoteNpcInteraction pendingRemoteNpcInteraction;
        private static bool clientDonkeyRequestPending;
        private static float clientDonkeyRequestSentAt;

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
            public readonly List<DeferredDialogueOp> BufferedDialogueOps = new List<DeferredDialogueOp>();
        }

        /// <summary>Subscribe to network events.</summary>
        public static void Enable()
        {
            clientDonkeyRequestPending = false;
            clientDonkeyRequestSentAt = 0f;
            var p2p = SteamP2PManager.Instance;
            if (p2p != null)
            {
                p2p.OnNpcInteractionReceived -= OnRemoteNpcInteraction;
                p2p.OnNpcInteractionReceived += OnRemoteNpcInteraction;
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
            }
            EndHeadlessDonkeyFlow();
            isProcessingRemote = false;
            isRemoteRequestedDonkeyFlowActive = false;
            pendingRemoteNpcInteraction = null;
            clientDonkeyRequestPending = false;
            clientDonkeyRequestSentAt = 0f;
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
                }

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
                    CoopMod.Logger.LogInfo("[NpcInteractionSync] Client requested host-authoritative donkey interaction");
                    SteamP2PManager.Instance?.SendNpcInteraction(tag, objId, pos, zoneId);
                    SendCutsceneWalkToLocalPlayer();
                    return false;
                }

                CoopMod.Logger.LogInfo($"[NpcInteractionSync] Local interact with NPC tag='{tag}', obj_id='{objId}' — broadcasting");
                SteamP2PManager.Instance?.SendNpcInteraction(tag, objId, pos, zoneId);

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
        private static void OnRemoteNpcInteraction(CSteamID senderID, string tag, string objId, Vector3 expectedPos, string originZoneId, bool hasScope)
        {
            try
            {
                var onlineCoop = OnlineCoopManager.Instance;
                if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled) return;
                if (!onlineCoop.IsRemotePlayer(senderID)) return;

                bool isDonkey = IsDonkeyIdentity(tag, objId);

                if (isDonkey && onlineCoop.IsHost)
                {
                    if (isRemoteRequestedDonkeyFlowActive)
                    {
                        CoopMod.Logger.LogInfo("[NpcInteractionSync] Ignoring duplicate request while host-authoritative donkey flow is active");
                        return;
                    }

                    isRemoteRequestedDonkeyFlowActive = true;
                    bool hostInScope = ShouldActivatePendingNpcInteraction(
                        expectedPos,
                        originZoneId,
                        hasScope,
                        out string hostScopeReason);
                    if (!hostInScope)
                    {
                        BeginHeadlessDonkeyFlow(
                            expectedPos,
                            originZoneId,
                            hasScope,
                            hostScopeReason);
                    }

                    if (!ExecuteHostAuthoritativeDonkeyInteraction(tag, objId, expectedPos, originZoneId))
                    {
                        EndHeadlessDonkeyFlow();
                        CompleteRemoteRequestedDonkeyFlow("host interaction failed");
                        return;
                    }

                    // Donkey.Interact runs the canonical FlowScript and applies
                    // its own cinematic lock when the host is actually nearby.
                    // An out-of-scope host runs that same flow headlessly.
                    return;
                }

                if (isDonkey)
                {
                    clientDonkeyRequestPending = false;
                    clientDonkeyRequestSentAt = 0f;
                    CoopMod.Logger.LogInfo("[NpcInteractionSync] Host accepted donkey interaction request");
                }

                if (!ShouldActivatePendingNpcInteraction(expectedPos, originZoneId, hasScope, out string deferReason))
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
                        ReceivedAt = Time.realtimeSinceStartup
                    };

                    string senderName = SteamFriends.GetFriendPersonaName(senderID);
                    CoopMod.Logger.LogInfo($"[NpcInteractionSync] Remote player '{senderName}' interacted with NPC '{GetPendingNpcLabel(pendingRemoteNpcInteraction)}' outside local scope — deferring ({deferReason})");
                    return;
                }

                if (!ReplayRemoteNpcInteraction(senderID, tag, objId, expectedPos, isDonkey))
                {
                    return;
                }

                // Lock the local player into the cutscene (control disabled + letterbox),
                // same as FlowScript cutscenes. Unlocked when the host's dialogue ends.
                Patches.CutsceneSyncPatches.LockLocalPlayerForCutscene();
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[NpcInteractionSync] Error handling remote NPC interaction: {ex}");
            }
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

                WorldGameObject remotePlayerWgo = onlineCoop.GetRemotePlayer();
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

        private static bool ExecuteHostAuthoritativeDonkeyInteraction(
            string tag,
            string objId,
            Vector3 expectedPos,
            string originZoneId)
        {
            WorldGameObject donkey = ResolveNpc(tag, objId, expectedPos);
            WorldGameObject localPlayer = MainGame.me?.player;
            if (donkey == null || localPlayer == null)
            {
                CoopMod.Logger.LogWarning("[NpcInteractionSync] Cannot execute client donkey request on host — donkey/player unavailable");
                return false;
            }

            DonkeyCartCorpseVisualGuard.Ensure(donkey);

            // Acknowledge before running Interact because FireEvent can synchronously
            // start dialogue and send its first packet.
            SteamP2PManager.Instance?.SendNpcInteraction(
                donkey.custom_tag ?? tag ?? "",
                donkey.obj_id ?? objId ?? "",
                donkey.transform.position,
                originZoneId ?? "");

            try
            {
                isProcessingRemote = true;
                CoopMod.Logger.LogInfo("[NpcInteractionSync] Host executing canonical donkey interaction requested by client");
                donkey.Interact(localPlayer, true);
                return true;
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[NpcInteractionSync] Host donkey interaction failed: {ex}");
                return false;
            }
            finally
            {
                isProcessingRemote = false;
            }
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

        internal static bool ShouldBlockLocalDialogueInput()
        {
            // A client-requested donkey interaction is executed canonically on the
            // host. When that host is out of scope, the canonical FlowScript still
            // creates local bubbles even though the host is only an observer.
            return pendingRemoteNpcInteraction != null ||
                   (isRemoteRequestedDonkeyFlowActive && isHeadlessDonkeyFlowActive);
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

            bool isDonkey = IsDonkeyIdentity(pending.Tag, pending.ObjId);
            if (!ReplayRemoteNpcInteraction(pending.SenderID, pending.Tag, pending.ObjId, pending.NpcWorldPos, isDonkey))
            {
                return;
            }

            CutsceneSyncPatches.LockLocalPlayerForCutscene();
            if (pending.HasJoinWorldPos)
            {
                OnlineCoopManager.Instance?.StartDeferredCutsceneWalk(pending.SenderID, pending.JoinWorldPos, pending.Facing);
            }

            // Buffered dialogue ops are not fast-forwarded — since the interaction
            // isn't replayed, there's no local dialogue UI to advance. The dialogue
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

        private static void BeginHeadlessDonkeyFlow(
            Vector3 originWorldPos,
            string originZoneId,
            bool hasScope,
            string scopeReason)
        {
            isHeadlessDonkeyFlowActive = true;
            headlessDonkeyFlowStartedAt = Time.realtimeSinceStartup;
            headlessDonkeyOriginWorldPos = originWorldPos;
            headlessDonkeyOriginZoneId = originZoneId ?? "";
            headlessDonkeyHasScope = hasScope;
            StoreRemoteDonkeyOriginalSpeed();
            CoopMod.Logger.LogInfo(
                $"[NpcInteractionSync] Host running remote-requested donkey flow without local cinematic ({scopeReason})");
        }

        private static void EndHeadlessDonkeyFlow()
        {
            if (!isHeadlessDonkeyFlowActive)
            {
                return;
            }

            RestoreRemoteDonkeyOriginalSpeed();
            isHeadlessDonkeyFlowActive = false;
            headlessDonkeyFlowStartedAt = 0f;
            headlessDonkeyOriginWorldPos = Vector3.zero;
            headlessDonkeyOriginZoneId = "";
            headlessDonkeyHasScope = false;
            CoopMod.Logger.LogInfo("[NpcInteractionSync] Headless donkey flow finished");
        }

        private static bool IsHeadlessDonkeyFlowActive()
        {
            if (!isHeadlessDonkeyFlowActive)
            {
                return false;
            }

            if (Time.realtimeSinceStartup - headlessDonkeyFlowStartedAt > HeadlessDonkeyFlowTimeout)
            {
                CoopMod.Logger.LogWarning("[NpcInteractionSync] Headless donkey flow timed out - clearing suppression");
                EndHeadlessDonkeyFlow();
                CompleteRemoteRequestedDonkeyFlow("headless flow timed out");
                return false;
            }

            return true;
        }

        public static void TickRemoteDonkeyStateSync()
        {
            if (IsHeadlessDonkeyFlowActive() &&
                ShouldActivatePendingNpcInteraction(
                    headlessDonkeyOriginWorldPos,
                    headlessDonkeyOriginZoneId,
                    headlessDonkeyHasScope,
                    out string joinReason))
            {
                EndHeadlessDonkeyFlow();
                var onlineCoop = OnlineCoopManager.Instance;
                WorldGameObject remotePlayer = onlineCoop?.GetRemotePlayer();
                CSteamID senderID =
                    onlineCoop?.RemotePlayerSteamID ?? CSteamID.Nil;
                byte facing = 0;
                try
                {
                    var remoteCharacter =
                        remotePlayer?.components?.character;
                    if (remoteCharacter != null)
                    {
                        facing = (byte)remoteCharacter.anim_direction;
                    }
                }
                catch { }

                CutsceneSyncPatches.LockLocalPlayerForCutscene();
                if (remotePlayer != null && senderID != CSteamID.Nil)
                {
                    onlineCoop.BeginObservedCutsceneFollow(senderID);
                    onlineCoop.StartLiveCutsceneJoinWalk(
                        senderID,
                        remotePlayer.transform.position,
                        facing);
                }
                CoopMod.Logger.LogInfo(
                    $"[NpcInteractionSync] Host joined and started walking to " +
                    $"the active donkey cutscene ({joinReason})");
            }
            TickPendingRemoteNpcInteraction();
        }

        public static bool ShouldBypassCutsceneWalkScopeCheck()
        {
            // A remote-requested donkey flow may run headlessly on an out-of-range
            // host. Its later walk packet must still obey normal participation scope.
            return false;
        }

        private static void StoreRemoteDonkeyOriginalSpeed()
        {
            var localPlayer = MainGame.me?.player;
            if (localPlayer?.data == null)
            {
                remoteDonkeySpeedStored = false;
                return;
            }

            float currentSpeed = localPlayer.data.GetParam("speed", LazyConsts.PLAYER_SPEED);
            remoteDonkeyOriginalSpeed = Mathf.Max(currentSpeed, LazyConsts.PLAYER_SPEED);
            remoteDonkeySpeedStored = true;
            CoopMod.Logger.LogInfo($"[NpcInteractionSync] Stored local speed before remote donkey sync: {remoteDonkeyOriginalSpeed}");
        }

        private static void RestoreRemoteDonkeyOriginalSpeed()
        {
            var localPlayer = MainGame.me?.player;
            if (remoteDonkeySpeedStored && localPlayer?.data != null)
            {
                localPlayer.data.SetParam("speed", remoteDonkeyOriginalSpeed);
                CoopMod.Logger.LogInfo($"[NpcInteractionSync] Restored local speed after remote donkey sync: {remoteDonkeyOriginalSpeed}");
            }

            remoteDonkeySpeedStored = false;
        }

        private static void CompleteRemoteRequestedDonkeyFlow(string reason)
        {
            if (!isRemoteRequestedDonkeyFlowActive)
            {
                return;
            }

            isRemoteRequestedDonkeyFlowActive = false;
            OnlineCoopManager.Instance?.EndObservedCutsceneFollow(
                "host-authoritative donkey flow completed");

            // A client-requested donkey interaction is executed by the host, so the
            // requesting client has no local FlowScript completion callback. DialogueEnd
            // is the existing completion contract for observed NPC interactions and
            // releases that client's cinematic lock.
            var dialogueSync = DialogueSync.Instance;
            if (dialogueSync != null)
            {
                dialogueSync.NotifyAuthoritativeDialogueEnd();
            }
            else
            {
                SteamP2PManager.Instance?.SendDialogueEnd();
            }

            CoopMod.Logger.LogInfo(
                $"[NpcInteractionSync] Host-authoritative donkey flow completed ({reason}); notified remote participant");
        }

        // A client may initiate the authoritative donkey flow while the host is
        // elsewhere. The host still runs story/world mutations, but must not lose
        // control or receive letterboxing for a cutscene it is not attending.
        [HarmonyPatch(typeof(GS), nameof(GS.SetPlayerEnable))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static bool GS_SetPlayerEnable_Prefix(bool player_enabled, bool affect_cinematic)
        {
            if (!affect_cinematic)
            {
                return true;
            }

            bool headlessFlowActive = IsHeadlessDonkeyFlowActive();
            if (player_enabled && isRemoteRequestedDonkeyFlowActive)
            {
                if (headlessFlowActive)
                {
                    EndHeadlessDonkeyFlow();
                }

                CompleteRemoteRequestedDonkeyFlow("canonical flow restored player control");

                // The out-of-scope host never received the matching disable call, so
                // preserve the existing suppression of its unmatched cinematic enable.
                return !headlessFlowActive;
            }

            if (headlessFlowActive)
            {
                if (!player_enabled)
                {
                    CoopMod.Logger.LogInfo("[NpcInteractionSync] Suppressed out-of-scope host donkey cinematic lock");
                }
                return false;
            }

            return true;
        }

        [HarmonyPatch(typeof(GS), nameof(GS.AffectCinematic))]
        [HarmonyPrefix]
        public static bool GS_AffectCinematic_Prefix(bool show)
        {
            if (IsHeadlessDonkeyFlowActive())
            {
                CoopMod.Logger.LogInfo(
                    $"[NpcInteractionSync] Suppressed out-of-scope host donkey cinematic overlay show={show}");
                return false;
            }

            return true;
        }

        [HarmonyPatch(typeof(CameraTools), nameof(CameraTools.TweenLetterbox))]
        [HarmonyPrefix]
        public static bool CameraTools_TweenLetterbox_Prefix(bool show)
        {
            if (IsHeadlessDonkeyFlowActive())
            {
                CoopMod.Logger.LogInfo(
                    $"[NpcInteractionSync] Suppressed out-of-scope host donkey letterbox show={show}");
                return false;
            }

            return true;
        }
    }
}

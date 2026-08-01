using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;
using Steamworks;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Synchronizes dialogue and speech bubbles between online co-op players.
    /// Features:
    /// - Both players see the same dialogue when one initiates conversation (if close enough)
    /// - Either player can advance dialogue during cutscenes
    /// - Speech bubbles appear above the player who advanced the dialogue
    /// </summary>
    public class DialogueSync : SyncBehaviour
    {
        public static DialogueSync Instance => GetInstance<DialogueSync>();

        private const string MSG_DIALOGUE_START = "DLG_START:";
        private const string MSG_DIALOGUE_ADVANCE = "DLG_ADVANCE";
        private const string MSG_DIALOGUE_CHOICE = "DLG_CHOICE:";
        private const string MSG_DIALOGUE_END = "DLG_END";
        private const string MSG_SPEECH_BUBBLE = "DLG_BUBBLE:";
        private const float SpeechDuplicateSuppressSeconds = 0.75f;
        private const float DIALOGUE_SYNC_DISTANCE = 1000f;

        public bool IsInSyncedDialogue { get; private set; }
        private CSteamID dialogueInitiator;
        public CSteamID LastDialogueAdvancer { get; private set; }
        public bool LocalPlayerLastAdvanced { get; private set; }
        public static bool IsApplyingAmbientSpeech { get; private set; }

        private readonly Dictionary<string, float> recentSpeech = new Dictionary<string, float>();
        private readonly HashSet<int> remotelyAdvancedBubbles = new HashSet<int>();
        private int pendingRemoteAdvances;
        private bool remoteDialogueEndPending;
        private CSteamID pendingRemoteOptionSender = CSteamID.Nil;
        private List<AnswerVisualData> pendingRemoteOptions;
        private bool pendingRemoteOptionsShowToLeft;
        private int pendingRemoteHoverIndex = -1;
        private MethodInfo sayMethod;
        private Type bubbleTypeEnumType;
        private Type voiceIdEnumType;

        protected override string LogPrefix => "[DialogueSync]";

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnDialogueMessage -= OnDialogueMessageReceived;
                SteamP2PManager.Instance.OnDialogueMessage += OnDialogueMessageReceived;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnDialogueMessage -= OnDialogueMessageReceived;
            }
        }

        protected override void OnSyncEnabled()
        {
            ResetState();
        }

        protected override void OnSyncDisabled()
        {
            ResetState();
        }

        public bool ArePlayersCloseEnough()
        {
            if (!IsOnline) return false;

            var localPlayer = MainGame.me?.player;
            OnlineCoopManager online = OnlineCoopManager.Instance;
            if (localPlayer == null || online == null)
                return false;

            List<KeyValuePair<CSteamID, PlayerComponent>> remotes =
                online.GetRemotePlayersSnapshot();
            for (int i = 0; i < remotes.Count; i++)
            {
                PlayerComponent remote = remotes[i].Value;
                if (remote != null &&
                    Vector3.Distance(
                        localPlayer.transform.position,
                        remote.transform.position) <= DIALOGUE_SYNC_DISTANCE)
                {
                    return true;
                }
            }
            return false;
        }

        public void NotifyDialogueStart(string npcId)
        {
            if (!ShouldSyncDialogue()) return;

            CoopMod.Logger.LogInfo($"{LogPrefix} Local player started dialogue with {npcId}");

            dialogueInitiator = SteamUser.GetSteamID();
            LastDialogueAdvancer = dialogueInitiator;
            LocalPlayerLastAdvanced = true;
            IsInSyncedDialogue = true;

            string message = $"{MSG_DIALOGUE_START}{npcId}";
            BroadcastDialogueMessage(message);
        }

        public void NotifyDialogueAdvance()
        {
            CoopMod.Logger.LogInfo($"{LogPrefix} NotifyDialogueAdvance called");

            if (!ShouldSyncDialogue())
            {
                CoopMod.Logger.LogInfo($"{LogPrefix} ShouldSyncDialogue returned false - not sending");
                return;
            }

            CoopMod.Logger.LogInfo($"{LogPrefix} Local player advanced dialogue - broadcasting to remote");

            EnsureDialogueSession(SteamUser.GetSteamID());
            LastDialogueAdvancer = SteamUser.GetSteamID();
            LocalPlayerLastAdvanced = true;

            BroadcastDialogueMessage(MSG_DIALOGUE_ADVANCE);
        }

        /// <summary>
        /// Give a cutscene that starts independently on both peers the same initial
        /// dialogue owner. The first player to advance a bubble takes ownership from
        /// here through the normal dialogue-advance messages.
        /// </summary>
        public void EnsureCutsceneDialogueOwner(CSteamID owner)
        {
            if (!IsOnline || owner == CSteamID.Nil)
                return;
            if (IsInSyncedDialogue && LastDialogueAdvancer != CSteamID.Nil)
                return;

            dialogueInitiator = owner;
            LastDialogueAdvancer = owner;
            LocalPlayerLastAdvanced = owner == SteamUser.GetSteamID();
            IsInSyncedDialogue = true;
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Established shared cutscene dialogue owner: " +
                $"{SteamFriends.GetFriendPersonaName(owner)} " +
                $"(local={LocalPlayerLastAdvanced})");
        }

        public void NotifyDialogueChoice(int choiceIndex, string choiceText)
        {
            if (!ShouldSyncDialogue()) return;

            CoopMod.Logger.LogInfo($"{LogPrefix} Local player chose option {choiceIndex}: {choiceText}");

            EnsureDialogueSession(SteamUser.GetSteamID());
            LastDialogueAdvancer = SteamUser.GetSteamID();
            LocalPlayerLastAdvanced = true;

            string message = $"{MSG_DIALOGUE_CHOICE}{choiceIndex}|{choiceText}";
            BroadcastDialogueMessage(message);
        }

        public void NotifyDialogueOptions(
            List<AnswerVisualData> answers,
            bool showToLeft)
        {
            if (!ShouldSyncDialogue() || answers == null)
                return;

            List<AnswerVisualData> visibleAnswers =
                GetVisibleAnswers(answers);
            if (visibleAnswers.Count == 0)
                return;

            CSteamID localID = SteamUser.GetSteamID();
            EnsureDialogueSession(localID);
            dialogueInitiator = localID;
            LastDialogueAdvancer = localID;
            LocalPlayerLastAdvanced = true;

            SteamP2PManager.Instance?.SendDialogueOptions(
                visibleAnswers,
                showToLeft);
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Broadcast {visibleAnswers.Count} visible dialogue options");
        }

        public void NotifyDialogueHover(int choiceIndex)
        {
            if (!IsOnline ||
                !IsInSyncedDialogue ||
                LastDialogueAdvancer != SteamUser.GetSteamID())
            {
                return;
            }

            SteamP2PManager.Instance?.SendDialogueHover(choiceIndex);
        }

        public void NotifyDialogueEnd()
        {
            if (!IsInSyncedDialogue) return;

            BroadcastDialogueEnd("Dialogue ended");
        }

        internal void NotifyAuthoritativeDialogueEnd()
        {
            BroadcastDialogueEnd("Authoritative dialogue ended");
        }

        private void BroadcastDialogueEnd(string logMessage)
        {
            CoopMod.Logger.LogInfo($"{LogPrefix} {logMessage}");
            Patches.NpcInteractionSyncPatches
                .NotifyLocalNpcInteractionDialogueEnded();

            IsInSyncedDialogue = false;
            dialogueInitiator = CSteamID.Nil;
            pendingRemoteAdvances = 0;
            remotelyAdvancedBubbles.Clear();
            remoteDialogueEndPending = false;

            BroadcastDialogueMessage(MSG_DIALOGUE_END);
        }

        public void SyncSpeechBubble(long speakerId, string text, bool isPlayer)
        {
            if (!IsOnline || string.IsNullOrEmpty(text)) return;

            string speakerKey = BuildSpeakerKey(speakerId, string.Empty, Vector3.zero, 0, isPlayer);
            RecordRecentSpeech(speakerKey, text);
            SteamP2PManager.Instance?.SendDialogueBubble(speakerKey, text);
        }

        public void NotifyAmbientSpeechBubble(WorldGameObject speaker, string text, int bubbleType, bool sayAsPlayer)
        {
            if (IsApplyingAmbientSpeech || speaker == null || string.IsNullOrEmpty(text) || !IsOnline) return;
            if (Patches.CutsceneSyncPatches.ShouldSuppressAmbientSpeechRelay()) return;

            bool isPlayerSpeech = sayAsPlayer || speaker.is_player;

            long uniqueId = speaker.is_player ? 0L : speaker.unique_id;
            Vector3 position = speaker.transform != null ? speaker.transform.position : speaker.pos3;
            string speakerKey = BuildSpeakerKey(uniqueId, speaker.obj_id ?? string.Empty, position, bubbleType, isPlayerSpeech);
            RecordRecentSpeech(speakerKey, text);
            SteamP2PManager.Instance?.SendDialogueBubble(speakerKey, text);
        }

        private bool ShouldSyncDialogue()
        {
            if (!IsOnline) return false;

            var gameLoadSync = GameLoadSync.Instance;
            if (gameLoadSync != null && gameLoadSync.IsInIntroPhase)
            {
                CoopMod.Logger.LogInfo($"{LogPrefix} In intro phase - syncing dialogue regardless of distance");
                return true;
            }

            try
            {
                var pc = MainGame.me?.player_char;
                if (pc != null && !pc.control_enabled)
                    return true;
            }
            catch { }

            return ArePlayersCloseEnough();
        }

        private void BroadcastDialogueMessage(string message)
        {
            var lobbyID = SteamLobbyManager.Instance?.CurrentLobbyID ?? CSteamID.Nil;
            if (lobbyID == CSteamID.Nil) return;

            CSteamID myID = SteamUser.GetSteamID();
            int memberCount = SteamMatchmaking.GetNumLobbyMembers(lobbyID);

            for (int i = 0; i < memberCount; i++)
            {
                CSteamID memberID = SteamMatchmaking.GetLobbyMemberByIndex(lobbyID, i);
                if (memberID != myID)
                {
                    SteamP2PManager.Instance?.SendDialogueMessage(memberID, message);
                }
            }
        }

        private void OnDialogueMessageReceived(CSteamID senderID, Network.Op op, Network.MsgReader reader)
        {
            switch (op)
            {
                case Network.Op.DialogueStart:
                    string npcId = reader.ReadString();
                    HandleRemoteDialogueStart(senderID, npcId);
                    break;

                case Network.Op.DialogueAdvance:
                    HandleRemoteDialogueAdvance(senderID);
                    break;

                case Network.Op.DialogueChoice:
                    int choiceIndex = reader.ReadInt32();
                    string choiceText = reader.ReadString();
                    HandleRemoteDialogueChoice(senderID, choiceIndex, choiceText);
                    break;

                case Network.Op.DialogueOptions:
                    bool showToLeft = reader.ReadBool();
                    int optionCount = reader.ReadByte();
                    if (optionCount <= 0 || optionCount > 64)
                    {
                        CoopMod.Logger.LogWarning(
                            $"{LogPrefix} Ignoring invalid remote dialogue option count {optionCount}");
                        break;
                    }

                    var options = new List<AnswerVisualData>(optionCount);
                    for (int i = 0; i < optionCount; i++)
                        options.Add(ReadAnswerVisualData(ref reader, 0));
                    HandleRemoteDialogueOptions(
                        senderID,
                        options,
                        showToLeft);
                    break;

                case Network.Op.DialogueHover:
                    HandleRemoteDialogueHover(
                        senderID,
                        reader.ReadInt32());
                    break;

                case Network.Op.DialogueEnd:
                    HandleRemoteDialogueEnd(senderID);
                    break;

                case Network.Op.DialogueBubble:
                    string speakerId = reader.ReadString();
                    string text = reader.ReadString();
                    HandleRemoteSpeechBubble(senderID, speakerId, text);
                    break;
            }
        }

        private void HandleRemoteDialogueStart(CSteamID senderID, string npcId)
        {
            string senderName = SteamFriends.GetFriendPersonaName(senderID);
            CoopMod.Logger.LogInfo($"{LogPrefix} {senderName} started dialogue with {npcId}");

            dialogueInitiator = senderID;
            LastDialogueAdvancer = senderID;
            LocalPlayerLastAdvanced = false;
            IsInSyncedDialogue = true;
        }

        private void HandleRemoteDialogueAdvance(CSteamID senderID)
        {
            string senderName = SteamFriends.GetFriendPersonaName(senderID);
            CoopMod.Logger.LogInfo($"{LogPrefix} {senderName} advanced dialogue");

            EnsureDialogueSession(senderID);
            LastDialogueAdvancer = senderID;
            LocalPlayerLastAdvanced = false;

            if (Patches.CutsceneSyncPatches.TryBufferRemoteDialogueAdvance(senderID) ||
                Patches.NpcInteractionSyncPatches.TryBufferRemoteDialogueAdvance(senderID))
            {
                return;
            }

            QueueRemoteDialogueAdvance();
        }

        private void HandleRemoteDialogueChoice(CSteamID senderID, int choiceIndex, string choiceText)
        {
            string senderName = SteamFriends.GetFriendPersonaName(senderID);
            CoopMod.Logger.LogInfo($"{LogPrefix} {senderName} chose option {choiceIndex}: {choiceText}");

            EnsureDialogueSession(senderID);
            LastDialogueAdvancer = senderID;
            LocalPlayerLastAdvanced = false;
            ClearPendingRemoteOptions(senderID);

            if (Patches.OnlineDialoguePatches
                .CloseRemoteAnswerPresentation(senderID, choiceIndex))
            {
                return;
            }

            if (Patches.CutsceneSyncPatches.TryBufferRemoteDialogueChoice(senderID, choiceIndex, choiceText) ||
                Patches.NpcInteractionSyncPatches.TryBufferRemoteDialogueChoice(senderID, choiceIndex, choiceText))
            {
                return;
            }

            ApplyRemoteDialogueChoiceNow(choiceIndex);
        }

        private void HandleRemoteDialogueOptions(
            CSteamID senderID,
            List<AnswerVisualData> options,
            bool showToLeft)
        {
            EnsureDialogueSession(senderID);
            dialogueInitiator = senderID;
            LastDialogueAdvancer = senderID;
            LocalPlayerLastAdvanced = false;

            if (Patches.NpcInteractionSyncPatches
                .IsRemoteNpcInteractionDeferred(senderID))
            {
                pendingRemoteOptionSender = senderID;
                pendingRemoteOptions = options;
                pendingRemoteOptionsShowToLeft = showToLeft;
                pendingRemoteHoverIndex = -1;
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Deferred remote answer presentation until the local player enters the interaction");
                return;
            }

            Patches.OnlineDialoguePatches.ShowRemoteAnswerPresentation(
                senderID,
                options,
                showToLeft);
        }

        internal bool ShowPendingRemoteDialogueOptions(
            CSteamID senderID)
        {
            if (pendingRemoteOptions == null ||
                pendingRemoteOptionSender != senderID)
            {
                return false;
            }

            List<AnswerVisualData> options = pendingRemoteOptions;
            bool showToLeft = pendingRemoteOptionsShowToLeft;
            int hoverIndex = pendingRemoteHoverIndex;
            ClearPendingRemoteOptions(senderID);
            Patches.OnlineDialoguePatches.ShowRemoteAnswerPresentation(
                senderID,
                options,
                showToLeft);
            Patches.OnlineDialoguePatches.ApplyRemoteAnswerHover(
                senderID,
                hoverIndex);
            return true;
        }

        internal void ClearPendingRemoteOptions(CSteamID senderID)
        {
            if (pendingRemoteOptionSender != senderID &&
                senderID != CSteamID.Nil)
            {
                return;
            }

            pendingRemoteOptionSender = CSteamID.Nil;
            pendingRemoteOptions = null;
            pendingRemoteOptionsShowToLeft = false;
            pendingRemoteHoverIndex = -1;
        }

        private void HandleRemoteDialogueHover(
            CSteamID senderID,
            int choiceIndex)
        {
            if (pendingRemoteOptionSender == senderID &&
                pendingRemoteOptions != null)
            {
                pendingRemoteHoverIndex = choiceIndex;
                return;
            }

            Patches.OnlineDialoguePatches.ApplyRemoteAnswerHover(
                senderID,
                choiceIndex);
        }

        private void HandleRemoteDialogueEnd(CSteamID senderID)
        {
            string senderName = SteamFriends.GetFriendPersonaName(senderID);
            CoopMod.Logger.LogInfo($"{LogPrefix} {senderName} ended dialogue");
            ClearPendingRemoteOptions(senderID);
            Patches.OnlineDialoguePatches
                .CloseRemoteAnswerPresentation(senderID, -1);
            Patches.NpcInteractionSyncPatches
                .NotifyObservedNpcInteractionEnded(senderID);

            if (Patches.CutsceneSyncPatches.HasActiveMirroredLocalCutscene())
            {
                remoteDialogueEndPending = true;
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Deferring remote dialogue end until the " +
                    "mirrored local FlowScript catches up");
                return;
            }

            IsInSyncedDialogue = false;
            dialogueInitiator = CSteamID.Nil;

            bool endedDeferredCutscene = Patches.CutsceneSyncPatches.TryEndDeferredRemoteCutscene(senderID);
            bool endedDeferredNpcInteraction = Patches.NpcInteractionSyncPatches.TryEndDeferredRemoteNpcInteraction(senderID);
            if (Patches.CutsceneSyncPatches.ShouldKeepLocalPlayerLocked(senderID))
            {
                CoopMod.Logger.LogInfo($"{LogPrefix} Keeping local player locked because the remote FlowScript continues after dialogue");
            }
            else if (Patches.CutsceneSyncPatches.HasLiveRemoteCutsceneSession(senderID))
            {
                CoopMod.Logger.LogInfo($"{LogPrefix} Remote FlowScript continues outside local participation; no control state to release");
            }
            else if (endedDeferredCutscene || endedDeferredNpcInteraction)
            {
                CoopMod.Logger.LogInfo($"{LogPrefix} Remote dialogue ended outside local participation; no control state to release");
            }
            else
            {
                Patches.CutsceneSyncPatches.UnlockLocalPlayerAfterCutscene();
            }
        }

        private void HandleRemoteSpeechBubble(CSteamID senderID, string speakerId, string bubbleText)
        {
            if (string.IsNullOrEmpty(bubbleText) || !IsOnline) return;

            if (!TryParseSpeakerKey(speakerId, out long uniqueId, out string objId, out Vector3 position, out int bubbleType, out bool sayAsPlayer))
                return;

            string fingerprint = BuildSpeechFingerprint(speakerId, bubbleText);
            if (WasRecentlySeen(fingerprint)) return;

            WorldGameObject speaker = ResolveSpeechSpeaker(senderID, uniqueId, objId, position, sayAsPlayer);
            if (speaker == null)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Could not resolve ambient speech speaker uid={uniqueId} obj='{objId}'");
                return;
            }

            try
            {
                InvokeSay(speaker, bubbleText, bubbleType, sayAsPlayer);
                RecordRecentSpeech(speakerId, bubbleText);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Failed to apply ambient speech bubble: {ex.Message}");
            }
        }

        public void ApplyRemoteDialogueAdvanceNow()
        {
            if (SpeechBubbleGUI.all != null && SpeechBubbleGUI.all.Count > 0)
            {
                foreach (var kvp in SpeechBubbleGUI.all)
                {
                    var bubble = kvp.Value;
                    if (bubble != null && bubble.gameObject.activeInHierarchy)
                    {
                        try
                        {
                            bubble.ForceHide(false);
                            CoopMod.Logger.LogInfo($"{LogPrefix} Triggered speech bubble ForceHide");
                        }
                        catch (Exception ex)
                        {
                            CoopMod.Logger.LogWarning($"{LogPrefix} Error hiding speech bubble: {ex.Message}");
                        }
                        break;
                    }
                }
            }
        }

        private void QueueRemoteDialogueAdvance()
        {
            pendingRemoteAdvances++;
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Queued remote dialogue advance " +
                $"(pending={pendingRemoteAdvances})");
            TryApplyPendingRemoteAdvance();
        }

        /// <summary>
        /// Called after ShowMessage creates a fresh bubble. Remote input can arrive
        /// while the previous bubble is disappearing and before this one exists; in
        /// that gap it must remain queued rather than being discarded.
        /// </summary>
        public void NotifySpeechBubbleShown(SpeechBubbleGUI bubble)
        {
            if (bubble == null)
                return;

            remotelyAdvancedBubbles.Remove(bubble.GetInstanceID());
            TryApplyPendingRemoteAdvance();
        }

        public void NotifySpeechBubbleDestroyed(SpeechBubbleGUI bubble)
        {
            if (bubble != null)
                remotelyAdvancedBubbles.Remove(bubble.GetInstanceID());
        }

        public void NotifyMirroredLocalCutsceneFinished()
        {
            pendingRemoteAdvances = 0;
            remotelyAdvancedBubbles.Clear();
            bool deferredRemoteEnd = remoteDialogueEndPending;
            remoteDialogueEndPending = false;
            IsInSyncedDialogue = false;
            dialogueInitiator = CSteamID.Nil;
            LastDialogueAdvancer = CSteamID.Nil;
            LocalPlayerLastAdvanced = false;
            CoopMod.Logger.LogInfo(
                deferredRemoteEnd
                    ? $"{LogPrefix} Applied deferred remote dialogue end after the mirrored local FlowScript completed"
                    : $"{LogPrefix} Closed mirrored cutscene dialogue session when the local FlowScript completed");
        }

        private bool TryApplyPendingRemoteAdvance()
        {
            if (pendingRemoteAdvances <= 0 || SpeechBubbleGUI.all == null)
                return false;

            foreach (var kvp in SpeechBubbleGUI.all)
            {
                SpeechBubbleGUI bubble = kvp.Value;
                if (bubble == null || !bubble.gameObject.activeInHierarchy)
                    continue;

                int instanceId = bubble.GetInstanceID();
                if (remotelyAdvancedBubbles.Contains(instanceId))
                    continue;

                try
                {
                    remotelyAdvancedBubbles.Add(instanceId);
                    bubble.ForceHide(false);
                    pendingRemoteAdvances--;
                    CoopMod.Logger.LogInfo(
                        $"{LogPrefix} Applied queued remote dialogue advance " +
                        $"(pending={pendingRemoteAdvances})");
                    return true;
                }
                catch (Exception ex)
                {
                    remotelyAdvancedBubbles.Remove(instanceId);
                    CoopMod.Logger.LogWarning(
                        $"{LogPrefix} Error applying queued dialogue advance: " +
                        ex.Message);
                    return false;
                }
            }

            return false;
        }

        public void ApplyRemoteDialogueChoiceNow(int choiceIndex)
        {
            try
            {
                var multiAnswer =
                    Patches.OnlineDialoguePatches.GetCurrentMultiAnswer();
                if (multiAnswer != null && multiAnswer.gameObject.activeInHierarchy)
                {
                    var answersField = typeof(MultiAnswerGUI).GetField("_answers",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    if (answersField != null)
                    {
                        var answers = answersField.GetValue(multiAnswer) as List<MultiAnswerOptionGUI>;
                        if (answers != null && choiceIndex >= 0 && choiceIndex < answers.Count)
                        {
                            Patches.OnlineDialoguePatches.SetRemoteChoiceApplication();
                            answers[choiceIndex].OnChosen();
                            CoopMod.Logger.LogInfo($"{LogPrefix} Triggered multi-answer choice {choiceIndex}");
                        }
                        else
                        {
                            CoopMod.Logger.LogWarning($"{LogPrefix} Choice index {choiceIndex} out of range (answers count: {answers?.Count ?? 0})");
                        }
                    }
                    else
                    {
                        CoopMod.Logger.LogWarning($"{LogPrefix} Could not find _answers field on MultiAnswerGUI");
                    }
                }
                else
                {
                    CoopMod.Logger.LogWarning($"{LogPrefix} No active MultiAnswerGUI found for remote choice");
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"{LogPrefix} Error triggering choice: {ex.Message}");
            }
        }

        public void ResetState()
        {
            Patches.OnlineDialoguePatches
                .CloseAnyRemoteAnswerPresentation();
            IsInSyncedDialogue = false;
            dialogueInitiator = CSteamID.Nil;
            LastDialogueAdvancer = CSteamID.Nil;
            LocalPlayerLastAdvanced = false;
            pendingRemoteAdvances = 0;
            remotelyAdvancedBubbles.Clear();
            remoteDialogueEndPending = false;
            ClearPendingRemoteOptions(CSteamID.Nil);
            CoopMod.Logger.LogInfo($"{LogPrefix} State reset");
        }

        private static List<AnswerVisualData> GetVisibleAnswers(
            List<AnswerVisualData> answers)
        {
            var result = new List<AnswerVisualData>();
            var save = MainGame.me?.save;

            for (int i = 0; i < answers.Count && result.Count < 64; i++)
            {
                AnswerVisualData answer = answers[i];
                if (answer == null || string.IsNullOrEmpty(answer.id))
                    continue;

                bool unlocked =
                    answer.id[0] != '@' ||
                    (save?.unlocked_phrases != null &&
                     save.unlocked_phrases.Contains(answer.id));
                bool blacklisted =
                    save?.black_list_of_phrases != null &&
                    save.black_list_of_phrases.Contains(answer.id);

                if (unlocked && !blacklisted)
                    result.Add(answer);
            }

            return result;
        }

        private static AnswerVisualData ReadAnswerVisualData(
            ref MsgReader reader,
            int depth)
        {
            if (depth > 1)
                throw new InvalidOperationException(
                    "Remote dialogue option nesting exceeds the supported depth");

            bool multiple = reader.ReadBool() && depth == 0;
            AnswerVisualData answer = multiple
                ? (AnswerVisualData)new MultipleAnswerVisualData()
                : new AnswerVisualData();

            answer.id = reader.ReadString();
            answer.icon_price = reader.ReadString();
            answer.icon_lock = reader.ReadString();
            answer.icon_reward = reader.ReadString();
            answer.can_be_picked = reader.ReadBool();
            answer.inside_price_is_red = reader.ReadBool();
            answer.price_txt = reader.ReadString();
            answer.icon_price_quality = reader.ReadString();
            answer.icon_reward_quality = reader.ReadString();
            answer.icon_lock_quality = reader.ReadString();
            answer.n_price = reader.ReadInt32();
            answer.n_reward = reader.ReadInt32();
            answer.n_lock = reader.ReadInt32();

            int childCount = reader.ReadByte();
            if (childCount > 32)
                throw new InvalidOperationException(
                    $"Remote dialogue option has invalid child count {childCount}");

            for (int i = 0; i < childCount; i++)
                answer.answer_visual_datas.Add(
                    ReadAnswerVisualData(ref reader, depth + 1));

            return answer;
        }

        private void EnsureDialogueSession(CSteamID advancer)
        {
            if (IsInSyncedDialogue)
                return;

            IsInSyncedDialogue = true;
            if (dialogueInitiator == CSteamID.Nil)
                dialogueInitiator = advancer;

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Established implicit synced dialogue session from dialogue advancement");
        }

        private WorldGameObject ResolveSpeechSpeaker(CSteamID senderID, long uniqueId, string objId, Vector3 position, bool sayAsPlayer)
        {
            bool isPlayerSpeech = uniqueId == 0L || sayAsPlayer;
            if (isPlayerSpeech)
            {
                WorldGameObject remotePlayer = OnlineCoopManager.Instance
                    ?.GetRemotePlayer(senderID);
                if (remotePlayer == null)
                {
                    remotePlayer = OnlineCoopManager.Instance
                        ?.GetRemotePlayer(LastDialogueAdvancer);
                }
                if (remotePlayer != null) return remotePlayer;
            }

            WorldGameObject resolved = WGORegistry.Instance?.Resolve(uniqueId, objId, null, position, 160f);
            if (resolved != null) return resolved;

            // Never put unresolved NPC dialogue over the local player's head. A
            // newly spawned NPC may miss one bubble while its registry entry catches
            // up, but assigning that line to a player changes the cutscene's roles.
            return isPlayerSpeech ? MainGame.me?.player : null;
        }

        private void InvokeSay(WorldGameObject speaker, string text, int bubbleType, bool sayAsPlayer)
        {
            EnsureSayMethod();
            if (sayMethod == null) return;

            object bubbleEnum = bubbleTypeEnumType != null ? Enum.ToObject(bubbleTypeEnumType, bubbleType) : null;
            object voiceEnum = voiceIdEnumType != null ? Enum.ToObject(voiceIdEnumType, 0) : null;
            object[] args = new object[7] { text, null, null, bubbleEnum, voiceEnum, sayAsPlayer, null };

            IsApplyingAmbientSpeech = true;
            try
            {
                sayMethod.Invoke(speaker, args);
            }
            finally
            {
                IsApplyingAmbientSpeech = false;
            }
        }

        private void EnsureSayMethod()
        {
            if (sayMethod != null) return;

            MethodInfo[] methods = typeof(WorldGameObject).GetMethods(BindingFlags.Instance | BindingFlags.Public);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != "Say") continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length < 7) continue;

                sayMethod = method;
                bubbleTypeEnumType = parameters[3].ParameterType;
                voiceIdEnumType = parameters[4].ParameterType;
                return;
            }
        }

        private static string BuildSpeakerKey(long uniqueId, string objId, Vector3 position, int bubbleType, bool sayAsPlayer)
        {
            string encodedObjId = Convert.ToBase64String(Encoding.UTF8.GetBytes(objId ?? string.Empty));
            return string.Join("|", new[]
            {
                uniqueId.ToString(CultureInfo.InvariantCulture),
                encodedObjId,
                position.x.ToString("R", CultureInfo.InvariantCulture),
                position.y.ToString("R", CultureInfo.InvariantCulture),
                position.z.ToString("R", CultureInfo.InvariantCulture),
                bubbleType.ToString(CultureInfo.InvariantCulture),
                sayAsPlayer ? "1" : "0"
            });
        }

        private static bool TryParseSpeakerKey(string speakerKey, out long uniqueId, out string objId, out Vector3 position, out int bubbleType, out bool sayAsPlayer)
        {
            uniqueId = 0L;
            objId = string.Empty;
            position = Vector3.zero;
            bubbleType = 0;
            sayAsPlayer = false;

            string[] parts = (speakerKey ?? string.Empty).Split('|');
            if (parts.Length == 1)
                return long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uniqueId);

            if (parts.Length < 7) return false;

            if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uniqueId))
                return false;

            try { objId = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])); }
            catch { objId = string.Empty; }

            float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float x);
            float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float y);
            float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float z);
            int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out bubbleType);
            sayAsPlayer = parts[6] == "1";
            position = new Vector3(x, y, z);
            return true;
        }

        private void RecordRecentSpeech(string speakerKey, string text)
        {
            PruneRecentSpeech();
            recentSpeech[BuildSpeechFingerprint(speakerKey, text)] = Time.realtimeSinceStartup;
        }

        private bool WasRecentlySeen(string fingerprint)
        {
            PruneRecentSpeech();
            return recentSpeech.ContainsKey(fingerprint);
        }

        private void PruneRecentSpeech()
        {
            if (recentSpeech.Count == 0) return;

            float cutoff = Time.realtimeSinceStartup - SpeechDuplicateSuppressSeconds;
            var stale = new List<string>();
            foreach (var pair in recentSpeech)
            {
                if (pair.Value < cutoff) stale.Add(pair.Key);
            }

            for (int i = 0; i < stale.Count; i++)
                recentSpeech.Remove(stale[i]);
        }

        private static string BuildSpeechFingerprint(string speakerKey, string text)
        {
            return (speakerKey ?? string.Empty) + "\n" + (text ?? string.Empty);
        }
    }
}

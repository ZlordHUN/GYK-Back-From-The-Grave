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
            var remotePlayer = OnlineCoopManager.Instance?.GetRemotePlayer();

            if (localPlayer == null || remotePlayer == null)
                return false;

            float distance = Vector3.Distance(localPlayer.transform.position, remotePlayer.transform.position);
            return distance <= DIALOGUE_SYNC_DISTANCE;
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

            IsInSyncedDialogue = false;
            dialogueInitiator = CSteamID.Nil;

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

            ApplyRemoteDialogueAdvanceNow();
        }

        private void HandleRemoteDialogueChoice(CSteamID senderID, int choiceIndex, string choiceText)
        {
            string senderName = SteamFriends.GetFriendPersonaName(senderID);
            CoopMod.Logger.LogInfo($"{LogPrefix} {senderName} chose option {choiceIndex}: {choiceText}");

            EnsureDialogueSession(senderID);
            LastDialogueAdvancer = senderID;
            LocalPlayerLastAdvanced = false;

            if (Patches.CutsceneSyncPatches.TryBufferRemoteDialogueChoice(senderID, choiceIndex, choiceText) ||
                Patches.NpcInteractionSyncPatches.TryBufferRemoteDialogueChoice(senderID, choiceIndex, choiceText))
            {
                return;
            }

            ApplyRemoteDialogueChoiceNow(choiceIndex);
        }

        private void HandleRemoteDialogueEnd(CSteamID senderID)
        {
            string senderName = SteamFriends.GetFriendPersonaName(senderID);
            CoopMod.Logger.LogInfo($"{LogPrefix} {senderName} ended dialogue");

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

            WorldGameObject speaker = ResolveSpeechSpeaker(uniqueId, objId, position, sayAsPlayer);
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

        public void ApplyRemoteDialogueChoiceNow(int choiceIndex)
        {
            try
            {
                var multiAnswer = GUIElements.me?.multi_answer;
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
            IsInSyncedDialogue = false;
            dialogueInitiator = CSteamID.Nil;
            LastDialogueAdvancer = CSteamID.Nil;
            LocalPlayerLastAdvanced = false;
            CoopMod.Logger.LogInfo($"{LogPrefix} State reset");
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

        private WorldGameObject ResolveSpeechSpeaker(long uniqueId, string objId, Vector3 position, bool sayAsPlayer)
        {
            if (uniqueId == 0L || sayAsPlayer)
            {
                WorldGameObject remotePlayer = OnlineCoopManager.Instance?.GetRemotePlayer();
                if (remotePlayer != null) return remotePlayer;
            }

            WorldGameObject resolved = WGORegistry.Instance?.Resolve(uniqueId, objId, null, position, 160f);
            if (resolved != null) return resolved;

            return MainGame.me?.player;
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

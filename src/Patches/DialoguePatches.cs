using HarmonyLib;
using GraveyardKeeperCoop.UI;
using GraveyardKeeperCoop.Utils;
using GraveyardKeeperCoopMod.Utils;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches dialogue-related methods to capture and broadcast NPC dialogue and player choices to chat
    /// </summary>
    [HarmonyPatch]
    public static class DialoguePatches
    {
        /// <summary>
        /// Captures NPC and character dialogue when speech bubbles are shown
        /// </summary>
        [HarmonyPatch(typeof(SpeechBubbleGUI), nameof(SpeechBubbleGUI.ShowMessage), new System.Type[] {
            typeof(long), // speaker_id
            typeof(string), // txt
            typeof(UnityEngine.Transform), // link
            typeof(GJCommons.VoidDelegate), // on_disappeared
            typeof(bool), // show_to_left
            typeof(bool), // use_world_cam
            typeof(SpeechBubbleGUI.SpeechBubbleType), // type
            typeof(bool), // is_player
            typeof(SmartSpeechEngine.VoiceID) // voice
        })]
        [HarmonyPostfix]
        public static void CaptureDialogue(
            long speaker_id,
            string txt,
            SpeechBubbleGUI.SpeechBubbleType type,
            bool is_player)
        {
            try
            {
                if (CutsceneSyncPatches.ConsumeDeferredCatchUpDialogueCaptureSuppression())
                {
                    CoopMod.Logger.LogInfo("[DialoguePatch] Skipping deferred cutscene catch-up dialogue capture");
                    return;
                }

                if (ChatBubbleManager.IsShowingChatBubble)
                {
                    CoopMod.Logger.LogInfo("[DialoguePatch] Skipping chat speech bubble capture");
                    return;
                }

                // Check if we should capture this dialogue
                if (!DialogueHelper.ShouldSendToChat())
                    return;

                if (!DialogueHelper.ShouldCaptureNPCDialogue())
                    return;

                // Check if this bubble type should be logged
                if (!DialogueHelper.ShouldLogBubbleType(type))
                    return;

                // Rate limit to prevent spam
                if (!DialogueHelper.CanSendMessage())
                    return;

                // Get speaker name
                string speakerName;
                if (is_player)
                {
                    speakerName = GetOnlinePlayerSpeakerName(speaker_id);
                }
                else
                {
                    speakerName = DialogueHelper.GetSpeakerName(speaker_id);
                }

                // Clean the dialogue text
                string cleanText = DialogueHelper.CleanDialogueText(txt);

                // Skip empty messages
                if (string.IsNullOrWhiteSpace(cleanText))
                    return;

                // Format for chat
                string chatMessage = DialogueHelper.FormatNPCDialogue(speakerName, cleanText);

                // Add to chat system (ChatManager handles both local display and network broadcasting)
                ChatManager.AddMessage(chatMessage);

                CoopMod.Logger.LogInfo($"[DialoguePatch] Captured dialogue: {chatMessage}");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[DialoguePatch] Error in CaptureDialogue: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static string GetOnlinePlayerSpeakerName(long speakerId)
        {
            var onlineCoop = Network.OnlineCoopManager.Instance;
            if (onlineCoop != null && onlineCoop.IsOnlineCoopEnabled)
            {
                var remotes = onlineCoop.GetRemotePlayersSnapshot();
                for (int i = 0; i < remotes.Count; i++)
                {
                    WorldGameObject remotePlayer = remotes[i].Value?.wgo;
                    if (remotePlayer != null && remotePlayer.unique_id == speakerId)
                    {
                        return Steamworks.SteamFriends.GetFriendPersonaName(
                            remotes[i].Key);
                    }
                }
            }

            return SteamHelper.GetLocalPlayerName();
        }

        /// <summary>
        /// Captures player dialogue choices when they select an option
        /// </summary>
        [HarmonyPatch(typeof(MultiAnswerGUI), "OnChosen")]
        [HarmonyPostfix]
        public static void CapturePlayerChoice(string answer)
        {
            try
            {
                // Check if we should capture player choices
                if (!DialogueHelper.ShouldSendToChat())
                    return;

                if (!DialogueHelper.ShouldCapturePlayerChoices())
                    return;

                // Rate limit
                if (!DialogueHelper.CanSendMessage())
                    return;

                // Get player name
                string playerName = "Player";
                if (MainGame.me?.player != null)
                {
                    playerName = MainGame.me.player.name ?? "Player";
                }

                // Clean the choice text
                string cleanText = DialogueHelper.CleanDialogueText(answer);

                // Skip empty choices
                if (string.IsNullOrWhiteSpace(cleanText))
                    return;

                // Format for chat
                string chatMessage = DialogueHelper.FormatPlayerChoice(playerName, cleanText);

                // Add to chat system (ChatManager handles both local display and network broadcasting)
                ChatManager.AddMessage(chatMessage);

                CoopMod.Logger.LogInfo($"[DialoguePatch] Captured choice: {chatMessage}");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[DialoguePatch] Error in CapturePlayerChoice: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Optional: Capture corner dialogue (phone calls, special events)
        /// </summary>
        [HarmonyPatch(typeof(CornerTalkGUI), "Say")]
        [HarmonyPostfix]
        public static void CaptureCornerDialogue(string locale, string sprite)
        {
            try
            {
                if (!DialogueHelper.ShouldSendToChat())
                    return;

                if (!DialogueHelper.ShouldCaptureNPCDialogue())
                    return;

                if (!DialogueHelper.CanSendMessage())
                    return;

                // Clean text
                string cleanText = DialogueHelper.CleanDialogueText(locale);

                if (string.IsNullOrWhiteSpace(cleanText))
                    return;

                // Determine speaker from sprite (phone calls are usually special characters)
                string speakerName = "???"; // Mystery caller
                if (!string.IsNullOrEmpty(sprite))
                {
                    if (sprite.Contains("red_eye"))
                        speakerName = "Red Eye";
                    else if (sprite.Contains("phone"))
                        speakerName = "Caller";
                }

                // Format and send
                string chatMessage = DialogueHelper.FormatNPCDialogue(speakerName, cleanText);
                ChatManager.AddMessage(chatMessage);

                CoopMod.Logger.LogInfo($"[DialoguePatch] Captured corner dialogue: {chatMessage}");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogError($"[DialoguePatch] Error in CaptureCornerDialogue: {ex.Message}");
            }
        }
    }
}

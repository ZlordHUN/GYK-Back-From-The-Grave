using HarmonyLib;
using UnityEngine;
using GraveyardKeeperCoop.Multiplayer;
using GraveyardKeeperCoop.Network;
using System.Collections.Generic;
using Steamworks;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Patches for dialogue synchronization in online co-op.
    /// Allows both players to see the same dialogue (if close) and either can advance it.
    /// Speech bubbles appear above the player who advanced the dialogue.
    ///
    /// Bug fixes over previous implementation:
    /// 1. Uses DoAnimations prefix/postfix instead of LazyInput.GetKeyDown — catches mouse clicks
    ///    (the game checks Input.GetMouseButtonDown(0) directly, bypassing LazyInput)
    /// 2. Only broadcasts when bubble actually starts disappearing (not on typewriter skip)
    /// 3. Fixed MultiAnswerGUI reflection field names (_answers not _buttons, _answer_id not _answer)
    /// </summary>
    [HarmonyPatch]
    public class OnlineDialoguePatches
    {
        // Multi-answer choices are applied by invoking OnChosen directly, so they
        // need an explicit echo guard. Speech-bubble ForceHide does not: it runs
        // outside DoAnimations and therefore cannot look like a local transition
        // to the prefix/postfix pair below.
        private static bool applyingRemoteDialogueChoice = false;

        // Track which bubbles were NOT disappearing before DoAnimations ran.
        // If they become _disappearing=true AFTER DoAnimations, the local player dismissed them.
        private static HashSet<int> bubblesNotDisappearingBefore = new HashSet<int>();
        // DoAnimations clears LazyInput as soon as it sees an advance key. Capture
        // that input in the prefix so keyboard/controller advances are not
        // misclassified as timer-driven dismissals by the postfix.
        private static HashSet<int> bubblesWithAdvanceInputBefore = new HashSet<int>();
        // Bubbles created while applying a remote speech packet are observational.
        // Until the local player joins that scene, dismissing one must not drive the
        // authoritative dialogue forward.
        private static HashSet<int> remotelySynchronizedBubbles = new HashSet<int>();

        /// <summary>
        /// Track when local player initiates a dialogue via interaction
        /// </summary>
        // Ownership: this is an observer only. OnlineInteractionPatches and
        // LocalCoopDialoguePatches may suppress/route interactions before this runs;
        // only successful interactions by the real local online player are broadcast.
        [HarmonyPatch(typeof(InteractionComponent), "Interact")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        public static void InteractionComponent_Interact_Postfix(InteractionComponent __instance, bool __result)
        {
            if (!__result)
                return;

            if (!IsRealLocalOnlinePlayer(__instance?.wgo))
                return;

            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return;

            var dialogueSync = DialogueSync.Instance;
            if (dialogueSync == null)
                return;

            string npcId = __instance.wgo?.obj_id ?? "unknown";
            CoopMod.Logger.LogInfo($"[OnlineDialogue] Local player interacted with {npcId}");
            dialogueSync.NotifyDialogueStart(npcId);
        }

        private static bool IsRealLocalOnlinePlayer(WorldGameObject wgo)
        {
            if (wgo == null || !wgo.is_player)
                return false;

            return MainGame.me?.player != null && wgo == MainGame.me.player;
        }

        /// <summary>
        /// BEFORE DoAnimations: record which bubbles are NOT yet disappearing.
        /// DoAnimations is the private method on SpeechBubbleGUI that handles all input
        /// (LazyInput.GetKeyDown(Back), GetKeyDown(Select), AND Input.GetMouseButtonDown(0))
        /// and transitions the bubble through typewriter → hold → disappear phases.
        /// </summary>
        [HarmonyPatch(typeof(SpeechBubbleGUI), "DoAnimations")]
        [HarmonyPrefix]
        public static bool SpeechBubbleGUI_DoAnimations_Prefix(SpeechBubbleGUI __instance, bool ____disappearing)
        {
            // Only track if we're in online coop
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return true;

            // Record that this bubble was NOT disappearing before DoAnimations ran
            if (!____disappearing)
            {
                int instanceId = __instance.GetInstanceID();
                bool remoteCutsceneObserver =
                    remotelySynchronizedBubbles.Contains(instanceId) &&
                    CutsceneSyncPatches.IsAwaitingLocalParticipation();
                bool remoteNpcObserver =
                    NpcInteractionSyncPatches.ShouldBlockLocalDialogueInput();
                if ((remoteCutsceneObserver || remoteNpcObserver) &&
                    HasLocalAdvanceInput())
                {
                    // DoAnimations interprets any click/select/back input as either a
                    // typewriter skip or a dismissal. Skip the method for this input
                    // frame so an observer cannot mutate the bubble at all.
                    LazyInput.ClearAllKeysDown();
                    bubblesNotDisappearingBefore.Remove(instanceId);
                    bubblesWithAdvanceInputBefore.Remove(instanceId);
                    CoopMod.Logger.LogInfo(
                        "[OnlineDialogue] Blocked input on observed speech bubble until local player joins the cutscene");
                    return false;
                }

                bubblesNotDisappearingBefore.Add(instanceId);
                if (HasLocalAdvanceInput())
                {
                    bubblesWithAdvanceInputBefore.Add(instanceId);
                }
                else
                {
                    bubblesWithAdvanceInputBefore.Remove(instanceId);
                }
            }

            return true;
        }

        /// <summary>
        /// AFTER DoAnimations: check if the bubble just started disappearing.
        /// If it transitioned from not-disappearing to disappearing during DoAnimations,
        /// it means the LOCAL player advanced the dialogue (via any input method).
        ///
        /// This approach is superior to patching LazyInput.GetKeyDown because:
        /// - It catches mouse clicks (Input.GetMouseButtonDown(0) bypasses LazyInput)
        /// - It only fires when the bubble ACTUALLY dismisses (not on typewriter skip)
        /// - It works for keyboard, gamepad, AND mouse input
        /// </summary>
        [HarmonyPatch(typeof(SpeechBubbleGUI), "DoAnimations")]
        [HarmonyPostfix]
        public static void SpeechBubbleGUI_DoAnimations_Postfix(SpeechBubbleGUI __instance, bool ____disappearing)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return;

            int instanceId = __instance.GetInstanceID();

            // Check if this bubble just transitioned to disappearing DURING DoAnimations
            if (____disappearing && bubblesNotDisappearingBefore.Remove(instanceId))
            {
                // Only notify if the player actually pressed a button this frame.
                // FlowScript cutscenes auto-dismiss bubbles via timer — those should
                // NOT reset LocalPlayerLastAdvanced, or the remote redirect breaks.
                bool playerPressedButton = bubblesWithAdvanceInputBefore.Remove(instanceId);

                if (playerPressedButton)
                {
                    bool remoteBubble = remotelySynchronizedBubbles.Contains(instanceId);
                    bool remoteCutsceneObserver =
                        remoteBubble &&
                        CutsceneSyncPatches.IsAwaitingLocalParticipation();
                    bool remoteNpcObserver =
                        NpcInteractionSyncPatches.ShouldBlockLocalDialogueInput();
                    if (remoteCutsceneObserver || remoteNpcObserver)
                    {
                        CoopMod.Logger.LogInfo(
                            "[OnlineDialogue] Ignored click on observed speech bubble until local player joins the cutscene");
                    }
                    else
                    {
                        CoopMod.Logger.LogInfo("[OnlineDialogue] Local player dismissed speech bubble — broadcasting advance to remote");
                        DialogueSync.Instance?.NotifyDialogueAdvance();
                    }
                }
                else
                {
                    CoopMod.Logger.LogInfo("[OnlineDialogue] Bubble auto-dismissed (timer/script) — not broadcasting");
                }

                remotelySynchronizedBubbles.Remove(instanceId);
            }
            else if (!____disappearing)
            {
                // Bubble is still alive (e.g., typewriter skip happened but no dismiss)
                // Clean up tracking — will be re-added next frame by prefix
                bubblesNotDisappearingBefore.Remove(instanceId);
                bubblesWithAdvanceInputBefore.Remove(instanceId);
            }
            else
            {
                remotelySynchronizedBubbles.Remove(instanceId);
            }
        }

        /// <summary>
        /// Mark that a multi-answer choice is being invoked from remote sync.
        /// </summary>
        public static void SetRemoteChoiceApplication()
        {
            applyingRemoteDialogueChoice = true;
        }

        /// <summary>
        /// Patch MultiAnswerGUI to track when player makes a choice.
        /// Fixed: uses correct field names from decompiled code:
        ///   - MultiAnswerGUI._answers (not _buttons) — List&lt;MultiAnswerOptionGUI&gt;
        ///   - MultiAnswerOptionGUI._answer_id (not _answer) — string
        /// </summary>
        [HarmonyPatch(typeof(MultiAnswerGUI), "OnChosen")]
        [HarmonyPrefix]
        public static void MultiAnswerGUI_OnChosen_Prefix(string answer)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return;

            // If this choice is from local input (not remote), notify remote player
            if (!applyingRemoteDialogueChoice)
            {
                var multiAnswer = GUIElements.me?.multi_answer;
                if (multiAnswer != null)
                {
                    try
                    {
                        // _answers is List<MultiAnswerOptionGUI> on MultiAnswerGUI
                        var answersField = typeof(MultiAnswerGUI).GetField("_answers",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                        if (answersField != null)
                        {
                            var answers = answersField.GetValue(multiAnswer) as List<MultiAnswerOptionGUI>;
                            if (answers != null)
                            {
                                for (int i = 0; i < answers.Count; i++)
                                {
                                    // _answer_id is the string ID on MultiAnswerOptionGUI
                                    var answerIdField = typeof(MultiAnswerOptionGUI).GetField("_answer_id",
                                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                    if (answerIdField != null)
                                    {
                                        var optionId = answerIdField.GetValue(answers[i]) as string;
                                        if (optionId == answer)
                                        {
                                            CoopMod.Logger.LogInfo($"[OnlineDialogue] Local player chose option {i}: {answer}");
                                            DialogueSync.Instance?.NotifyDialogueChoice(i, answer);
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            CoopMod.Logger.LogWarning("[OnlineDialogue] Could not find _answers field on MultiAnswerGUI");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        CoopMod.Logger.LogWarning($"[OnlineDialogue] Error finding choice index: {ex.Message}");
                    }
                }
            }
            else
            {
                CoopMod.Logger.LogInfo("[OnlineDialogue] Choice was remote-triggered — not echoing back");
            }

            applyingRemoteDialogueChoice = false;
        }

        /// <summary>
        /// Track when GUIs close (end of dialogue)
        /// </summary>
        [HarmonyPatch(typeof(BaseGUI), "Hide")]
        [HarmonyPostfix]
        public static void BaseGUI_Hide_Postfix(BaseGUI __instance)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return;

            if (TechSync.Instance?.ShouldSuppressDialogueEndForPresentation(__instance) == true)
            {
                CoopMod.Logger.LogInfo($"[OnlineDialogue] Shared {__instance.GetType().Name} closed — preserving active dialogue session");
                return;
            }

            if (BaseGUI.all_guis_closed)
            {
                bool noSpeechBubbles = SpeechBubbleGUI.all == null || SpeechBubbleGUI.all.Count == 0;
                if (noSpeechBubbles)
                {
                    DialogueSync.Instance?.NotifyDialogueEnd();
                }
            }
        }

        /// <summary>
        /// Route synchronized player speech to whichever player last advanced the
        /// dialogue. This must assign both directions: remotely replayed speech is
        /// initially linked to its sender, which may not be the current owner.
        /// </summary>
        [HarmonyPatch(typeof(SpeechBubbleGUI), "ShowMessage",
            new System.Type[] { typeof(long), typeof(string), typeof(Transform), typeof(GJCommons.VoidDelegate), typeof(bool), typeof(bool), typeof(SpeechBubbleGUI.SpeechBubbleType), typeof(bool), typeof(SmartSpeechEngine.VoiceID) })]
        [HarmonyPrefix]
        public static void SpeechBubbleGUI_ShowMessage_Prefix(ref long speaker_id, ref Transform link, bool is_player)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return;

            if (!is_player)
                return;

            var dialogueSync = DialogueSync.Instance;
            if (dialogueSync == null ||
                !dialogueSync.IsInSyncedDialogue ||
                dialogueSync.LastDialogueAdvancer == CSteamID.Nil)
                return;

            WorldGameObject owner = dialogueSync.LocalPlayerLastAdvanced
                ? MainGame.me?.player
                : onlineCoop.GetRemotePlayer();
            if (owner?.bubble_pos_tf != null)
            {
                speaker_id = owner.unique_id;
                link = owner.bubble_pos_tf;
                CoopMod.Logger.LogInfo(
                    dialogueSync.LocalPlayerLastAdvanced
                        ? "[OnlineDialogue] Routed speech bubble to local dialogue advancer"
                        : "[OnlineDialogue] Routed speech bubble to remote dialogue advancer");
            }
        }

        [HarmonyPatch(typeof(SpeechBubbleGUI), "ShowMessage",
            new System.Type[] { typeof(long), typeof(string), typeof(Transform), typeof(GJCommons.VoidDelegate), typeof(bool), typeof(bool), typeof(SpeechBubbleGUI.SpeechBubbleType), typeof(bool), typeof(SmartSpeechEngine.VoiceID) })]
        [HarmonyPostfix]
        public static void SpeechBubbleGUI_ShowMessage_Postfix(long speaker_id)
        {
            if (!DialogueSync.IsApplyingAmbientSpeech)
                return;

            if (SpeechBubbleGUI.all != null &&
                SpeechBubbleGUI.all.TryGetValue(speaker_id, out SpeechBubbleGUI bubble) &&
                bubble != null)
            {
                remotelySynchronizedBubbles.Add(bubble.GetInstanceID());
            }
        }

        /// <summary>
        /// Also patch MultiAnswerGUI to show at the correct player position
        /// </summary>
        [HarmonyPatch(typeof(MultiAnswerGUI), "ShowAnswers",
            new System.Type[] { typeof(List<AnswerVisualData>), typeof(Transform), typeof(MultiAnswerGUI.MultiAnswerResult), typeof(bool), typeof(GJCommons.VoidDelegate), typeof(WorldGameObject) })]
        [HarmonyPrefix]
        public static void MultiAnswerGUI_ShowAnswers_Prefix(ref Transform link)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return;

            var dialogueSync = DialogueSync.Instance;
            if (dialogueSync == null ||
                !dialogueSync.IsInSyncedDialogue ||
                dialogueSync.LastDialogueAdvancer == CSteamID.Nil)
                return;

            WorldGameObject owner = dialogueSync.LocalPlayerLastAdvanced
                ? MainGame.me?.player
                : onlineCoop.GetRemotePlayer();
            if (owner?.bubble_pos_tf != null)
            {
                link = owner.bubble_pos_tf;
                CoopMod.Logger.LogInfo(
                    dialogueSync.LocalPlayerLastAdvanced
                        ? "[OnlineDialogue] Routed multi-answer to local dialogue advancer"
                        : "[OnlineDialogue] Routed multi-answer to remote dialogue advancer");
            }
        }

        private static bool SafeGetKeyDown(KeyCode key)
        {
            try { return Input.GetKeyDown(key); }
            catch { return false; }
        }

        private static bool HasLocalAdvanceInput()
        {
            return Input.GetMouseButtonDown(0)
                || LazyInput.GetKeyDown(GameKey.Select)
                || LazyInput.GetKeyDown(GameKey.Back)
                || SafeGetKeyDown(KeyCode.Space)
                || SafeGetKeyDown(KeyCode.Return);
        }
    }
}

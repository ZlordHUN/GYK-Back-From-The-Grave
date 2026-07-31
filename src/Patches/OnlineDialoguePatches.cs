using HarmonyLib;
using UnityEngine;
using GraveyardKeeperCoop.Multiplayer;
using GraveyardKeeperCoop.Network;
using System.Collections.Generic;
using System.Reflection;
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
        private static bool applyingRemoteAnswerPresentation;
        private static MultiAnswerGUI remoteAnswerPresentation;
        private static CSteamID remoteAnswerSender = CSteamID.Nil;
        private static int remoteAnswerHoverIndex = -1;
        private static int localAnswerHoverIndex = -1;
        private static readonly FieldInfo CurrentAnswerGuiField =
            AccessTools.Field(typeof(MultiAnswerGUI), "_current");
        private static readonly FieldInfo AnswersField =
            AccessTools.Field(typeof(MultiAnswerGUI), "_answers");
        private static readonly FieldInfo AnswerIdField =
            AccessTools.Field(typeof(MultiAnswerOptionGUI), "_answer_id");
        private static readonly FieldInfo AnswerButtonField =
            AccessTools.Field(typeof(MultiAnswerOptionGUI), "_button");
        private static readonly FieldInfo AnswerWidgetField =
            AccessTools.Field(typeof(MultiAnswerOptionGUI), "_widget");
        private static readonly FieldInfo AnswerStartSizeField =
            AccessTools.Field(typeof(MultiAnswerOptionGUI), "_start_size");
        private static readonly FieldInfo AnswerFocusDeltaField =
            AccessTools.Field(typeof(MultiAnswerOptionGUI), "_focus_delta_size");
        private static readonly FieldInfo AnswerDefaultColorField =
            AccessTools.Field(typeof(MultiAnswerOptionGUI), "_default_color");

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
        public static void InteractionComponent_Interact_Postfix(
            InteractionComponent __instance,
            bool __result,
            WorldGameObject ____target_obj)
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

            // InteractionComponent also handles doors, teleports, containers, and
            // work objects. Treating every successful interaction as dialogue leaves
            // IsInSyncedDialogue stuck after non-dialogue scripts such as Teleport.
            // Actual speech can still establish an implicit session on its first
            // advance, so only NPC targets should eagerly open a dialogue session.
            WorldGameObject target = ____target_obj ?? __instance.nearest;
            if (target?.obj_def == null || !target.obj_def.IsNPC())
                return;

            // The client-side donkey Interact is suppressed until the host validates
            // it. Do not announce a dialogue session for that suppressed attempt;
            // NpcInteractionSync starts the session when approval arrives and the
            // player-bound FlowScript actually begins.
            if (NpcInteractionSyncPatches
                .ShouldSuppressPendingDialogueStart(target))
                return;

            string npcId = target.obj_id ?? "unknown";
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
                var multiAnswer = GetCurrentMultiAnswer();
                if (multiAnswer != null)
                {
                    try
                    {
                        if (AnswersField != null)
                        {
                            var answers = AnswersField.GetValue(multiAnswer) as List<MultiAnswerOptionGUI>;
                            if (answers != null)
                            {
                                for (int i = 0; i < answers.Count; i++)
                                {
                                    if (AnswerIdField != null)
                                    {
                                        var optionId = AnswerIdField.GetValue(answers[i]) as string;
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
                : onlineCoop.GetRemotePlayer(dialogueSync.LastDialogueAdvancer);
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
            SpeechBubbleGUI bubble = null;
            if (SpeechBubbleGUI.all != null &&
                SpeechBubbleGUI.all.TryGetValue(speaker_id, out bubble) &&
                bubble != null)
            {
                if (DialogueSync.IsApplyingAmbientSpeech)
                    remotelySynchronizedBubbles.Add(bubble.GetInstanceID());

                DialogueSync.Instance?.NotifySpeechBubbleShown(bubble);
            }
        }

        [HarmonyPatch(typeof(SpeechBubbleGUI), nameof(SpeechBubbleGUI.DestroyBubble))]
        [HarmonyPostfix]
        public static void SpeechBubbleGUI_DestroyBubble_Postfix(SpeechBubbleGUI __instance)
        {
            DialogueSync.Instance?.NotifySpeechBubbleDestroyed(__instance);
        }

        /// <summary>
        /// Also patch MultiAnswerGUI to show at the correct player position
        /// </summary>
        [HarmonyPatch(typeof(MultiAnswerGUI), "ShowAnswers",
            new System.Type[] { typeof(List<AnswerVisualData>), typeof(Transform), typeof(MultiAnswerGUI.MultiAnswerResult), typeof(bool), typeof(GJCommons.VoidDelegate), typeof(WorldGameObject) })]
        [HarmonyPrefix]
        public static void MultiAnswerGUI_ShowAnswers_Prefix(
            List<AnswerVisualData> answers,
            ref Transform link,
            bool show_to_left)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled)
                return;

            if (applyingRemoteAnswerPresentation)
                return;

            var dialogueSync = DialogueSync.Instance;
            if (dialogueSync == null)
                return;

            localAnswerHoverIndex = -1;
            if (NpcInteractionSyncPatches.HasLocalNpcVisualAuthority() ||
                (dialogueSync.IsInSyncedDialogue &&
                 dialogueSync.LocalPlayerLastAdvanced))
            {
                dialogueSync.NotifyDialogueOptions(
                    answers,
                    show_to_left);
            }

            if (!dialogueSync.IsInSyncedDialogue ||
                dialogueSync.LastDialogueAdvancer == CSteamID.Nil)
            {
                return;
            }

            WorldGameObject owner = dialogueSync.LocalPlayerLastAdvanced
                ? MainGame.me?.player
                : onlineCoop.GetRemotePlayer(dialogueSync.LastDialogueAdvancer);
            if (owner?.bubble_pos_tf != null)
            {
                link = owner.bubble_pos_tf;
                CoopMod.Logger.LogInfo(
                    dialogueSync.LocalPlayerLastAdvanced
                        ? "[OnlineDialogue] Routed multi-answer to local dialogue advancer"
                        : "[OnlineDialogue] Routed multi-answer to remote dialogue advancer");
            }
        }

        internal static MultiAnswerGUI GetCurrentMultiAnswer()
        {
            return CurrentAnswerGuiField?.GetValue(null) as MultiAnswerGUI;
        }

        internal static bool IsRemoteAnswerPresentation(
            MultiAnswerGUI gui)
        {
            return gui != null &&
                   remoteAnswerPresentation != null &&
                   gui == remoteAnswerPresentation;
        }

        public static void ShowRemoteAnswerPresentation(
            CSteamID senderID,
            List<AnswerVisualData> answers,
            bool showToLeft)
        {
            if (senderID == CSteamID.Nil ||
                answers == null ||
                answers.Count == 0)
            {
                return;
            }

            MultiAnswerGUI current = GetCurrentMultiAnswer();
            if (current != null &&
                current.gameObject.activeInHierarchy &&
                !IsRemoteAnswerPresentation(current))
            {
                CoopMod.Logger.LogWarning(
                    "[OnlineDialogue] Kept the local answer menu and ignored a competing remote presentation");
                return;
            }

            CloseAnyRemoteAnswerPresentation();

            WorldGameObject remotePlayer =
                OnlineCoopManager.Instance?.GetRemotePlayer(senderID);
            if (remotePlayer?.bubble_pos_tf == null)
            {
                CoopMod.Logger.LogWarning(
                    "[OnlineDialogue] Could not show remote answer menu because its player anchor is unavailable");
                return;
            }

            applyingRemoteAnswerPresentation = true;
            try
            {
                MultiAnswerGUI.ShowAnswers(
                    answers,
                    remotePlayer.bubble_pos_tf,
                    chosen => { },
                    showToLeft,
                    null,
                    remotePlayer);
                remoteAnswerPresentation = GetCurrentMultiAnswer();
                remoteAnswerSender = senderID;
                remoteAnswerHoverIndex = -1;

                if (remoteAnswerPresentation != null)
                {
                    CoopMod.Logger.LogInfo(
                        $"[OnlineDialogue] Showing {answers.Count} read-only dialogue options from " +
                        SteamFriends.GetFriendPersonaName(senderID));
                }
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    "[OnlineDialogue] Failed to show remote answer menu: " +
                    ex.Message);
                CloseAnyRemoteAnswerPresentation();
            }
            finally
            {
                applyingRemoteAnswerPresentation = false;
            }
        }

        public static void ApplyRemoteAnswerHover(
            CSteamID senderID,
            int choiceIndex)
        {
            if (remoteAnswerPresentation == null ||
                remoteAnswerSender != senderID ||
                AnswersField == null)
            {
                return;
            }

            var answers = AnswersField.GetValue(remoteAnswerPresentation)
                as List<MultiAnswerOptionGUI>;
            if (answers == null)
                return;

            int normalizedIndex =
                choiceIndex >= 0 && choiceIndex < answers.Count
                    ? choiceIndex
                    : -1;
            if (normalizedIndex == remoteAnswerHoverIndex)
                return;

            if (remoteAnswerHoverIndex >= 0 &&
                remoteAnswerHoverIndex < answers.Count)
            {
                SetAnswerFocused(
                    answers[remoteAnswerHoverIndex],
                    false);
            }

            remoteAnswerHoverIndex = normalizedIndex;
            if (remoteAnswerHoverIndex >= 0)
                SetAnswerFocused(answers[remoteAnswerHoverIndex], true);
        }

        public static bool CloseRemoteAnswerPresentation(
            CSteamID senderID,
            int selectedIndex)
        {
            if (remoteAnswerPresentation == null ||
                (senderID != CSteamID.Nil &&
                 remoteAnswerSender != senderID))
            {
                return false;
            }

            if (selectedIndex >= 0)
                ApplyRemoteAnswerHover(remoteAnswerSender, selectedIndex);

            string senderName =
                remoteAnswerSender == CSteamID.Nil
                    ? "remote player"
                    : SteamFriends.GetFriendPersonaName(remoteAnswerSender);
            remoteAnswerPresentation = null;
            remoteAnswerSender = CSteamID.Nil;
            remoteAnswerHoverIndex = -1;
            MultiAnswerGUI.HideAnyctive();
            CoopMod.Logger.LogInfo(
                $"[OnlineDialogue] Closed read-only dialogue options from {senderName}");
            return true;
        }

        public static void CloseAnyRemoteAnswerPresentation()
        {
            CloseRemoteAnswerPresentation(CSteamID.Nil, -1);
        }

        [HarmonyPatch(typeof(MultiAnswerGUI), "Update")]
        [HarmonyPrefix]
        public static bool MultiAnswerGUI_Update_InputGuard(
            MultiAnswerGUI __instance)
        {
            return !IsRemoteAnswerPresentation(__instance);
        }

        [HarmonyPatch(typeof(MultiAnswerOptionGUI), nameof(MultiAnswerOptionGUI.OnChosen))]
        [HarmonyPrefix]
        public static bool MultiAnswerOptionGUI_OnChosen_InputGuard(
            MultiAnswerOptionGUI __instance)
        {
            if (!IsRemoteAnswerOption(__instance))
                return true;

            LazyInput.ClearAllKeysDown();
            return false;
        }

        [HarmonyPatch(typeof(MultiAnswerOptionGUI), nameof(MultiAnswerOptionGUI.OnFocused))]
        [HarmonyPrefix]
        public static bool MultiAnswerOptionGUI_OnFocused_Prefix(
            MultiAnswerOptionGUI __instance)
        {
            if (IsRemoteAnswerOption(__instance))
                return false;

            NotifyLocalAnswerHover(__instance, true);
            return true;
        }

        [HarmonyPatch(typeof(MultiAnswerOptionGUI), nameof(MultiAnswerOptionGUI.OnUnfocused))]
        [HarmonyPrefix]
        public static bool MultiAnswerOptionGUI_OnUnfocused_Prefix(
            MultiAnswerOptionGUI __instance)
        {
            if (IsRemoteAnswerOption(__instance))
                return false;

            NotifyLocalAnswerHover(__instance, false);
            return true;
        }

        [HarmonyPatch(typeof(UIButtonColor), "OnHover")]
        [HarmonyPrefix]
        public static bool UIButton_OnHover_AnswerMirrorPrefix(
            UIButtonColor __instance,
            bool isOver)
        {
            MultiAnswerOptionGUI option =
                __instance?.GetComponentInParent<MultiAnswerOptionGUI>();
            if (option == null)
                return true;
            if (IsRemoteAnswerOption(option))
                return false;

            NotifyLocalAnswerHover(option, isOver);
            return true;
        }

        private static bool IsRemoteAnswerOption(
            MultiAnswerOptionGUI option)
        {
            return option != null &&
                   remoteAnswerPresentation != null &&
                   option.GetComponentInParent<MultiAnswerGUI>() ==
                       remoteAnswerPresentation;
        }

        private static void NotifyLocalAnswerHover(
            MultiAnswerOptionGUI option,
            bool focused)
        {
            MultiAnswerGUI current = GetCurrentMultiAnswer();
            if (current == null ||
                IsRemoteAnswerPresentation(current) ||
                AnswersField == null)
            {
                return;
            }

            var answers = AnswersField.GetValue(current)
                as List<MultiAnswerOptionGUI>;
            int index = answers?.IndexOf(option) ?? -1;
            if (index < 0)
                return;

            int nextIndex = focused
                ? index
                : (localAnswerHoverIndex == index ? -1 : localAnswerHoverIndex);
            if (nextIndex == localAnswerHoverIndex)
                return;

            localAnswerHoverIndex = nextIndex;
            DialogueSync.Instance?.NotifyDialogueHover(nextIndex);
        }

        private static void SetAnswerFocused(
            MultiAnswerOptionGUI option,
            bool focused)
        {
            try
            {
                var button = AnswerButtonField?.GetValue(option) as UIButton;
                var widget = AnswerWidgetField?.GetValue(option) as UIWidget;
                if (button == null || widget == null)
                    return;

                int startSize =
                    (int)(AnswerStartSizeField?.GetValue(option) ?? widget.width);
                int focusDelta =
                    (int)(AnswerFocusDeltaField?.GetValue(option) ?? 0);
                Color defaultColor =
                    (Color)(AnswerDefaultColorField?.GetValue(option) ??
                            button.defaultColor);

                button.defaultColor =
                    focused ? button.hover : defaultColor;
                widget.ChangeSize(
                    focused ? startSize + focusDelta : startSize,
                    widget.height,
                    0.1f,
                    null,
                    0f);
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    "[OnlineDialogue] Failed to mirror answer highlight: " +
                    ex.Message);
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

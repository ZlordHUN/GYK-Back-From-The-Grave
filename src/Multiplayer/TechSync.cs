using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    public class TechSync : PeriodicSyncBehaviour
    {
        public static TechSync Instance => GetInstance<TechSync>();

        private const byte PayloadVersion = 1;
        private const int MaxStringsPerList = 4096;
        private const int MaxIntsPerList = 512;
        private const string CraftUnlockPrefix = "craft:";
        private const string TechUnlockPrefix = "tech-unlock:";
        private const string TechPresentationPrefix = "tech-presentation:";
        private const string TutorialPresentationPrefix = "tutorial:";
        private const string PresentationAdvancePrefix = "presentation-advance:";
        private const string TechPresentationKind = "tech";
        private const string TutorialPresentationKind = "tutorial";
        private const int MaxTutorialIdLength = 128;
        private const float TechPresentationAggregationSeconds = 10f;

        protected override string LogPrefix => "[TechSync]";
        protected override float SyncIntervalSeconds => 5f;

        private static FieldInfo gameLogicsBlackListField;
        private static FieldInfo baseGuiOnHideField;
        private static FieldInfo tutorialOnClosedField;
        private bool sharedTechPopupOpen;
        private bool sharedTutorialOpen;
        private bool sharedTechGateAdvanced;
        private bool sharedTutorialGateAdvanced;
        private int lastSharedTechCloseFrame = -1;
        private int lastSharedTutorialCloseFrame = -1;
        private bool suppressLastSharedTechClose;
        private bool suppressLastSharedTutorialClose;
        private bool finishDialogueAfterLocalTutorialHide;
        private bool applyingUnlockEvent;
        private bool applyingRemoteTechPresentation;
        private bool applyingRemoteTutorialPresentation;
        private bool remoteTechPresentationActive;
        private bool sharedTechAggregatesUnlocks;
        private float techPresentationAggregationUntil;
        private string sharedTechId = string.Empty;
        private bool sharedTechReveal;
        private bool sharedTechPseudo;

        protected override void OnPeriodicSyncEnabled()
        {
            ResetPresentationState();
        }

        protected override void OnSyncDisabled()
        {
            ResetPresentationState();
        }

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnTechSyncReceived -= OnTechSyncReceived;
                SteamP2PManager.Instance.OnTechSyncReceived += OnTechSyncReceived;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnTechSyncReceived -= OnTechSyncReceived;
            }
        }

        protected override bool ShouldUpdate() =>
            MainGame.me != null && MainGame.game_started && MainGame.me.save != null;

        protected override bool ShouldSend() =>
            MainGame.me != null && MainGame.game_started && MainGame.me.save != null;


        protected override byte[] CaptureSnapshot(out string fingerprint)
        {
            var sb = new StringBuilder();
            var save = MainGame.me.save;

            using (var stream = new MemoryStream(8192))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(NextSequence++);

                WriteStringList(bw, save.unlocked_techs);
                sb.Append("t:").Append(save.unlocked_techs.Count).Append(';');
                WriteStringList(bw, save.unlocked_crafts);
                sb.Append("c:").Append(save.unlocked_crafts.Count).Append(';');
                WriteStringList(bw, save.locked_crafts);
                sb.Append("lc:").Append(save.locked_crafts.Count).Append(';');
                WriteStringList(bw, save.unlocked_works);
                sb.Append("w:").Append(save.unlocked_works.Count).Append(';');
                WriteStringList(bw, save.unlocked_phrases);
                sb.Append("p:").Append(save.unlocked_phrases.Count).Append(';');
                WriteStringList(bw, save.unlocked_perks);
                sb.Append("pk:").Append(save.unlocked_perks.Count).Append(';');
                WriteStringList(bw, save.black_list_of_phrases);
                sb.Append("bp:").Append(save.black_list_of_phrases.Count).Append(';');
                WriteStringList(bw, save.completed_one_time_crafts);
                sb.Append("otc:").Append(save.completed_one_time_crafts.Count).Append(';');
                WriteStringList(bw, save.revealed_techs);
                sb.Append("rt:").Append(save.revealed_techs.Count).Append(';');
                WriteStringList(bw, save.visible_techs);
                sb.Append("vt:").Append(save.visible_techs.Count).Append(';');
                WriteIntList(bw, save.unlocked_tech_branches);
                sb.Append("tb:").Append(save.unlocked_tech_branches.Count).Append(';');

                var blackList = GetGameLogicsBlackList(save.game_logics);
                WriteStringList(bw, blackList);
                sb.Append("gl");

                bw.Flush();
                fingerprint = sb.ToString();
                return stream.ToArray();
            }
        }

        protected override void BroadcastPayload(byte[] payload)
        {
            SteamP2PManager.Instance?.BroadcastTechSync(payload);
        }

        private static List<string> GetGameLogicsBlackList(GameLogics gameLogics)
        {
            if (gameLogics == null) return new List<string>();
            try
            {
                if (gameLogicsBlackListField == null)
                {
                    gameLogicsBlackListField = typeof(GameLogics).GetField("_black_list",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }
                if (gameLogicsBlackListField != null)
                {
                    return gameLogicsBlackListField.GetValue(gameLogics) as List<string> ?? new List<string>();
                }
            }
            catch { }
            return new List<string>();
        }

        private static void WriteStringList(BinaryWriter bw, List<string> list)
        {
            if (list == null)
            {
                bw.Write((ushort)0);
                return;
            }
            int count = Mathf.Min(list.Count, MaxStringsPerList);
            bw.Write((ushort)count);
            for (int i = 0; i < count; i++)
            {
                bw.Write(list[i] ?? "");
            }
        }

        private static void WriteIntList(BinaryWriter bw, List<int> list)
        {
            if (list == null)
            {
                bw.Write((ushort)0);
                return;
            }
            int count = Mathf.Min(list.Count, MaxIntsPerList);
            bw.Write((ushort)count);
            for (int i = 0; i < count; i++)
            {
                bw.Write(list[i]);
            }
        }

        private void OnTechSyncReceived(CSteamID senderID, byte[] payload)
        {
            if (payload == null || payload.Length == 0) return;

            string asText = null;
            try { asText = System.Text.Encoding.UTF8.GetString(payload); } catch { }

            if (asText != null && asText.StartsWith(CraftUnlockPrefix))
            {
                if (IsExpectedRemotePeer(senderID))
                {
                    HandleCraftUnlockEvent(senderID, asText.Substring(CraftUnlockPrefix.Length));
                }
                return;
            }

            if (asText != null && asText.StartsWith(TechUnlockPrefix))
            {
                if (IsExpectedRemotePeer(senderID))
                {
                    HandleTechUnlockEvent(asText.Substring(TechUnlockPrefix.Length));
                }
                return;
            }

            if (asText != null && asText.StartsWith(TechPresentationPrefix))
            {
                if (IsExpectedRemotePeer(senderID))
                {
                    HandleTechPresentation(
                        asText.Substring(TechPresentationPrefix.Length));
                }
                return;
            }

            if (asText != null && asText.StartsWith(TutorialPresentationPrefix))
            {
                if (IsExpectedRemotePeer(senderID))
                {
                    HandleTutorialPresentation(asText.Substring(TutorialPresentationPrefix.Length));
                }
                return;
            }

            if (asText != null && asText.StartsWith(PresentationAdvancePrefix))
            {
                if (IsExpectedRemotePeer(senderID))
                {
                    HandlePresentationAdvance(asText.Substring(PresentationAdvancePrefix.Length));
                }
                return;
            }

            TryReadPayload(payload, PayloadVersion, reader =>
            {
                if (!CheckSequence(reader, senderID)) return;

                ApplySnapshot(reader);
                ApplyEchoSuppress();
                ForceNextSend = true;
            }, "snapshot");
        }

        private void HandleCraftUnlockEvent(CSteamID senderID, string craftId)
        {
            if (string.IsNullOrEmpty(craftId)) return;
            if (MainGame.me?.save == null) return;

            if (MainGame.me.save.unlocked_crafts != null && MainGame.me.save.unlocked_crafts.Contains(craftId))
            {
                CoopMod.Logger.LogInfo($"{LogPrefix} Craft already unlocked: {craftId}");
                return;
            }

            applyingUnlockEvent = true;
            try
            {
                MainGame.me.save.UnlockCraft(craftId);
            }
            finally
            {
                applyingUnlockEvent = false;
            }
            RefreshTechDependentState();
            if (sharedTechPopupOpen ||
                GUIElements.me?.tech_dialog?.is_shown == true ||
                IsTechPresentationAggregationActive)
            {
                // A remotely applied tech can grant extra crafts from its apply
                // script after the aggregate popup has closed. Apply the state,
                // but do not present the same unlock a second time.
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Applied craft unlock without redundant popup during aggregate tech presentation: {craftId}");
            }
            else
            {
                ShowBlueprintPopup(craftId);
            }
            CoopMod.Logger.LogInfo($"{LogPrefix} Applied craft unlock event: {craftId}");
        }

        private void HandleTechUnlockEvent(string techId)
        {
            if (string.IsNullOrEmpty(techId) || MainGame.me?.save == null)
                return;

            if (MainGame.me.save.unlocked_techs != null &&
                MainGame.me.save.unlocked_techs.Contains(techId))
            {
                CoopMod.Logger.LogInfo($"{LogPrefix} Tech already unlocked: {techId}");
                return;
            }

            TechDefinition tech = GameBalance.me?.GetDataOrNull<TechDefinition>(techId);
            if (tech == null)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Cannot apply unknown tech unlock: {techId}");
                return;
            }

            applyingUnlockEvent = true;
            applyingRemoteTechPresentation = true;
            remoteTechPresentationActive = true;
            try
            {
                MainGame.me.save.UnlockTech(techId);
            }
            finally
            {
                applyingUnlockEvent = false;
                applyingRemoteTechPresentation = false;
            }

            RefreshTechDependentState();
            applyingRemoteTechPresentation = true;
            try
            {
                GUIElements.me?.tech_dialog?.Open(
                    tech,
                    null,
                    true,
                    false,
                    false,
                    true);
            }
            finally
            {
                applyingRemoteTechPresentation = false;
            }
            remoteTechPresentationActive = sharedTechPopupOpen;
            CoopMod.Logger.LogInfo($"{LogPrefix} Applied tech unlock event and popup: {techId}");
        }

        private void HandleTechPresentation(string payload)
        {
            if (string.IsNullOrEmpty(payload) || MainGame.me?.save == null)
                return;

            int separator = payload.IndexOf('|');
            if (separator <= 0 || separator >= payload.Length - 1)
                return;

            string presentationFlags = payload.Substring(0, separator);
            if (presentationFlags.Length < 1 ||
                presentationFlags.Length > 2 ||
                (presentationFlags[0] != '0' &&
                 presentationFlags[0] != '1') ||
                (presentationFlags.Length > 1 &&
                 presentationFlags[1] != '0' &&
                 presentationFlags[1] != '1'))
            {
                return;
            }

            bool revealTech = presentationFlags[0] == '1';
            bool showTechTreeAfter =
                presentationFlags.Length > 1 &&
                presentationFlags[1] == '1';
            string techId;
            try
            {
                techId = Encoding.UTF8.GetString(
                    Convert.FromBase64String(payload.Substring(separator + 1)));
            }
            catch
            {
                return;
            }

            TechDefinition tech = GameBalance.me?.GetDataOrNull<TechDefinition>(techId);
            if (tech == null)
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Cannot present unknown tech unlock: {techId}");
                return;
            }

            if (sharedTechPopupOpen &&
                string.Equals(sharedTechId, techId, StringComparison.Ordinal))
            {
                return;
            }

            ExtendTechPresentationAggregationWindow();
            applyingUnlockEvent = true;
            applyingRemoteTechPresentation = true;
            remoteTechPresentationActive = true;
            try
            {
                if (revealTech)
                {
                    MainGame.me.save.RevealHiddenTech(techId);
                }
                else if (MainGame.me.save.unlocked_techs == null ||
                         !MainGame.me.save.unlocked_techs.Contains(techId))
                {
                    MainGame.me.save.UnlockTech(techId);
                }
            }
            finally
            {
                applyingUnlockEvent = false;
                applyingRemoteTechPresentation = false;
            }

            RefreshTechDependentState();
            applyingRemoteTechPresentation = true;
            try
            {
                // State is applied above. Open as a pseudo-tech so this peer's OK
                // button only closes its independent presentation.
                GUIElements.me?.tech_dialog?.Open(
                    tech,
                    null,
                    true,
                    false,
                    showTechTreeAfter,
                    true);
            }
            finally
            {
                applyingRemoteTechPresentation = false;
            }
            remoteTechPresentationActive = sharedTechPopupOpen;

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Applied synchronized tech presentation: {techId} " +
                $"(show_tree_after={showTechTreeAfter})");
        }

        internal void BroadcastCraftUnlock(string craftId)
        {
            if (!IsSyncEnabled ||
                !IsOnline ||
                string.IsNullOrEmpty(craftId))
                return;

            if (applyingUnlockEvent ||
                applyingRemoteTechPresentation ||
                remoteTechPresentationActive ||
                IsTechPresentationAggregationActive)
            {
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Suppressed craft event covered by aggregate tech presentation: {craftId}");
                return;
            }

            SteamP2PManager.Instance?.BroadcastTechSyncUnlock(
                Encoding.UTF8.GetBytes(CraftUnlockPrefix + craftId));
            CoopMod.Logger.LogInfo($"{LogPrefix} Broadcast craft unlock event: {craftId}");
        }

        internal void BroadcastTechUnlock(string techId)
        {
            if (applyingUnlockEvent || !IsSyncEnabled || !IsOnline || string.IsNullOrEmpty(techId))
                return;

            SteamP2PManager.Instance?.BroadcastTechSyncUnlock(
                Encoding.UTF8.GetBytes(TechUnlockPrefix + techId));
            CoopMod.Logger.LogInfo($"{LogPrefix} Broadcast tech unlock event: {techId}");
        }

        private void ShowBlueprintPopup(string craftId)
        {
            try
            {
                TechDefinition techDef = BuildBlueprintTechDefinition(craftId);
                if (techDef == null) return;

                GUIElements.me?.tech_dialog?.Open(techDef, null, true, false, false, true);
                CoopMod.Logger.LogInfo($"{LogPrefix} Showed blueprint popup for craft: {craftId}");
            }
            catch (System.Exception ex)
            {
                CoopMod.Logger.LogDebug($"{LogPrefix} Could not show popup for craft '{craftId}': {ex.Message}");
            }
        }

        private void ApplySnapshot(BinaryReader reader)
        {
            if (MainGame.me == null || MainGame.me.save == null) return;

            var save = MainGame.me.save;

            try
            {
                save.unlocked_techs = ReadStringList(reader);
                save.unlocked_crafts = ReadStringList(reader);
                save.locked_crafts = ReadStringList(reader);
                save.unlocked_works = ReadStringList(reader);
                save.unlocked_phrases = ReadStringList(reader);
                save.unlocked_perks = ReadStringList(reader);
                save.black_list_of_phrases = ReadStringList(reader);
                save.completed_one_time_crafts = ReadStringList(reader);
                save.revealed_techs = ReadStringList(reader);
                save.visible_techs = ReadStringList(reader);
                save.unlocked_tech_branches = ReadIntList(reader);

                var blackList = ReadStringList(reader);
                SetGameLogicsBlackList(save.game_logics, blackList);

                RefreshTechDependentState();

                CoopMod.Logger.LogInfo($"{LogPrefix} Applied tech/perk progression: {save.unlocked_techs.Count} techs, {save.unlocked_perks.Count} perks, {save.unlocked_crafts.Count} crafts, {save.unlocked_tech_branches.Count} branches");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Error applying snapshot: {ex.Message}");
            }
        }

        private static TechDefinition BuildBlueprintTechDefinition(string craftId)
        {
            if (string.IsNullOrEmpty(craftId) || GameBalance.me == null)
                return null;

            // Object/build crafts are stored as ObjectCraftDefinition. Querying only
            // CraftDefinition returns null for entries such as the donkey picture
            // blueprint, which previously skipped the client's popup.
            CraftDefinition craftDef = GameBalance.me.GetDataOrNull<CraftDefinition>(craftId)
                                    ?? GameBalance.me.GetDataOrNull<ObjectCraftDefinition>(craftId);

            string labelId = craftId;
            if (craftDef?.output != null)
            {
                foreach (Item item in craftDef.output)
                {
                    if (item != null && item.id != "r" && item.id != "g" && item.id != "b")
                    {
                        labelId = item.id;
                        break;
                    }
                }
            }

            var techDef = new TechDefinition
            {
                id = labelId,
                price = new GameRes()
            };
            techDef.crafts.Add(craftId);
            return techDef;
        }

        internal void BroadcastTutorialPresentation(string tutorialId)
        {
            if (!IsSyncEnabled ||
                !IsOnline ||
                applyingRemoteTutorialPresentation ||
                string.IsNullOrEmpty(tutorialId))
                return;

            // These are opened manually from the pause menu rather than granted by
            // shared story progression.
            if (tutorialId == "controls" || tutorialId == "controls_pc")
                return;

            SteamP2PManager.Instance?.BroadcastTechSyncUnlock(
                Encoding.UTF8.GetBytes(TutorialPresentationPrefix + tutorialId));
            CoopMod.Logger.LogInfo($"{LogPrefix} Broadcast tutorial presentation: {tutorialId}");
        }

        private void HandleTutorialPresentation(string tutorialId)
        {
            if (string.IsNullOrEmpty(tutorialId) || tutorialId.Length > MaxTutorialIdLength)
                return;

            TutorialGUI tutorial = GUIElements.me?.tutorial;
            if (tutorial == null)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Cannot show remote tutorial '{tutorialId}' - TutorialGUI unavailable");
                return;
            }

            applyingRemoteTutorialPresentation = true;
            try
            {
                tutorial.Open(tutorialId, null);
            }
            finally
            {
                applyingRemoteTutorialPresentation = false;
            }
            CoopMod.Logger.LogInfo($"{LogPrefix} Showed synchronized tutorial: {tutorialId}");
        }

        internal void NotifyTechPopupOpened(
            TechDefinition tech,
            bool forcedUnlock,
            bool revealTech,
            bool showTechTreeAfter,
            bool pseudoTech)
        {
            if (!IsSyncEnabled || !IsOnline || !forcedUnlock)
                return;

            sharedTechPopupOpen = true;
            sharedTechId = tech?.id ?? string.Empty;
            sharedTechReveal = revealTech;
            sharedTechPseudo = pseudoTech;
            sharedTechAggregatesUnlocks =
                !pseudoTech || applyingRemoteTechPresentation;
            sharedTechGateAdvanced = false;
            lastSharedTechCloseFrame = -1;
            suppressLastSharedTechClose = false;
            if (sharedTechAggregatesUnlocks)
            {
                ExtendTechPresentationAggregationWindow();
            }

            if (!pseudoTech &&
                !applyingRemoteTechPresentation &&
                !string.IsNullOrEmpty(sharedTechId))
            {
                string encodedId = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(sharedTechId));
                SteamP2PManager.Instance?.BroadcastTechSyncUnlock(
                    Encoding.UTF8.GetBytes(
                        TechPresentationPrefix +
                        (revealTech ? "1" : "0") +
                        (showTechTreeAfter ? "1|" : "0|") +
                        encodedId));
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Broadcast tech presentation: {sharedTechId} " +
                    $"(show_tree_after={showTechTreeAfter})");
            }
        }

        internal void NotifyTutorialPopupOpened(string tutorialId)
        {
            if (!IsSyncEnabled || !IsOnline || string.IsNullOrEmpty(tutorialId))
                return;

            if (tutorialId == "controls" || tutorialId == "controls_pc")
                return;

            sharedTutorialOpen = true;
            sharedTutorialGateAdvanced = false;
            lastSharedTutorialCloseFrame = -1;
            suppressLastSharedTutorialClose = false;
            finishDialogueAfterLocalTutorialHide = false;
        }

        internal void NotifyPresentationClosing(BaseGUI gui)
        {
            if (!IsSyncEnabled || !IsOnline || gui == null)
                return;

            if (gui is TechUnlockDialogGUI && sharedTechPopupOpen)
            {
                if (sharedTechAggregatesUnlocks)
                {
                    ExtendTechPresentationAggregationWindow();
                }
                bool gateWasAlreadyAdvanced = sharedTechGateAdvanced;
                sharedTechPopupOpen = false;
                remoteTechPresentationActive = false;
                lastSharedTechCloseFrame = Time.frameCount;
                suppressLastSharedTechClose = !IsHost || gateWasAlreadyAdvanced;
                AdvancePresentationGateIfFirst(TechPresentationKind, ref sharedTechGateAdvanced);
                return;
            }

            if (gui is TutorialGUI && sharedTutorialOpen)
            {
                sharedTutorialOpen = false;
                lastSharedTutorialCloseFrame = Time.frameCount;
                // TutorialGUI invokes its FlowScript callback after BaseGUI.Hide
                // returns. Always suppress the generic BaseGUI dialogue-end check
                // and evaluate the completed callback from TutorialGUI.Hide's
                // concrete postfix instead. Either peer may own the FlowScript;
                // host-only completion leaves a client-owned interaction locked.
                suppressLastSharedTutorialClose = true;
                finishDialogueAfterLocalTutorialHide = true;
                AdvancePresentationGateIfFirst(TutorialPresentationKind, ref sharedTutorialGateAdvanced);
            }
        }

        internal void NotifyTutorialPopupHidden()
        {
            if (!finishDialogueAfterLocalTutorialHide)
                return;

            finishDialogueAfterLocalTutorialHide = false;
            TryFinishDialogueAfterLocalPresentation(null);
        }

        internal bool ShouldSuppressDialogueEndForPresentation(BaseGUI gui)
        {
            if (gui is TechUnlockDialogGUI)
            {
                return lastSharedTechCloseFrame == Time.frameCount &&
                       suppressLastSharedTechClose;
            }

            if (gui is TutorialGUI)
            {
                return lastSharedTutorialCloseFrame == Time.frameCount &&
                       suppressLastSharedTutorialClose;
            }

            return false;
        }

        private void AdvancePresentationGateIfFirst(string presentationKind, ref bool gateAdvanced)
        {
            if (gateAdvanced)
                return;

            gateAdvanced = true;
            SteamP2PManager.Instance?.BroadcastTechSyncUnlock(
                Encoding.UTF8.GetBytes(PresentationAdvancePrefix + presentationKind));
            CoopMod.Logger.LogInfo($"{LogPrefix} Broadcast shared presentation acknowledgement: {presentationKind}");
        }

        private void HandlePresentationAdvance(string presentationKind)
        {
            if (presentationKind == TechPresentationKind)
            {
                if (sharedTechGateAdvanced)
                    return;

                sharedTechGateAdvanced = true;
                CoopMod.Logger.LogInfo($"{LogPrefix} Remote player acknowledged blueprint popup; local popup remains open");
                InvokeAndClearLocalPresentationCallback(TechPresentationKind);
                return;
            }

            if (presentationKind == TutorialPresentationKind)
            {
                if (sharedTutorialGateAdvanced)
                    return;

                sharedTutorialGateAdvanced = true;
                CoopMod.Logger.LogInfo($"{LogPrefix} Remote player acknowledged tutorial; local tutorial remains open");
                InvokeAndClearLocalPresentationCallback(TutorialPresentationKind);
            }
        }

        private void InvokeAndClearLocalPresentationCallback(string presentationKind)
        {
            try
            {
                GJCommons.VoidDelegate callback = null;

                if (presentationKind == TechPresentationKind && sharedTechPopupOpen)
                {
                    ApplyPendingLocalTechUnlock();

                    if (baseGuiOnHideField == null)
                    {
                        baseGuiOnHideField = typeof(BaseGUI).GetField(
                            "_on_hide",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                    }

                    TechUnlockDialogGUI popup = GUIElements.me?.tech_dialog;
                    if (popup != null && baseGuiOnHideField != null)
                    {
                        callback = baseGuiOnHideField.GetValue(popup) as GJCommons.VoidDelegate;
                        baseGuiOnHideField.SetValue(popup, null);
                    }
                }
                else if (presentationKind == TutorialPresentationKind && sharedTutorialOpen)
                {
                    if (tutorialOnClosedField == null)
                    {
                        tutorialOnClosedField = typeof(TutorialGUI).GetField(
                            "_on_closed",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                    }

                    TutorialGUI tutorial = GUIElements.me?.tutorial;
                    if (tutorial != null && tutorialOnClosedField != null)
                    {
                        callback = tutorialOnClosedField.GetValue(tutorial) as GJCommons.VoidDelegate;
                        tutorialOnClosedField.SetValue(tutorial, null);
                    }
                }

                if (callback == null)
                {
                    // Observer-side synchronized popups intentionally have no
                    // FlowScript callback. Only the canonical peer advances.
                    return;
                }

                callback();
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Advanced local {presentationKind} gate without closing its local popup");
                TryFinishDialogueAfterLocalPresentation(
                    presentationKind == TechPresentationKind
                        ? (BaseGUI)GUIElements.me?.tech_dialog
                        : GUIElements.me?.tutorial);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Failed to advance local {presentationKind} gate: {ex.Message}");
            }
        }

        private void ApplyPendingLocalTechUnlock()
        {
            if (sharedTechPseudo ||
                string.IsNullOrEmpty(sharedTechId) ||
                MainGame.me?.save == null)
            {
                return;
            }

            ExtendTechPresentationAggregationWindow();
            applyingUnlockEvent = true;
            try
            {
                if (sharedTechReveal)
                {
                    MainGame.me.save.RevealHiddenTech(sharedTechId);
                }
                else if (MainGame.me.save.unlocked_techs == null ||
                         !MainGame.me.save.unlocked_techs.Contains(sharedTechId))
                {
                    MainGame.me.save.UnlockTech(sharedTechId);
                }
            }
            finally
            {
                applyingUnlockEvent = false;
            }
            RefreshTechDependentState();
        }

        internal void NotifyTechUnlockStarting(string techId)
        {
            if (!sharedTechPopupOpen ||
                !sharedTechAggregatesUnlocks ||
                string.IsNullOrEmpty(techId) ||
                !string.Equals(sharedTechId, techId, StringComparison.Ordinal))
            {
                return;
            }

            ExtendTechPresentationAggregationWindow();
        }

        internal bool ShouldSuppressDuplicateSharedTechUnlock(string techId)
        {
            if (!IsSyncEnabled ||
                !IsOnline ||
                !sharedTechPopupOpen ||
                !sharedTechGateAdvanced ||
                sharedTechPseudo ||
                string.IsNullOrEmpty(techId) ||
                !string.Equals(sharedTechId, techId, StringComparison.Ordinal) ||
                MainGame.me?.save?.unlocked_techs == null ||
                !MainGame.me.save.unlocked_techs.Contains(techId))
            {
                return false;
            }

            ExtendTechPresentationAggregationWindow();
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Suppressed duplicate shared tech execution after remote acknowledgement: {techId}");
            return true;
        }

        private bool IsTechPresentationAggregationActive =>
            Time.realtimeSinceStartup < techPresentationAggregationUntil;

        private void ExtendTechPresentationAggregationWindow()
        {
            techPresentationAggregationUntil =
                Time.realtimeSinceStartup + TechPresentationAggregationSeconds;
        }

        private void TryFinishDialogueAfterLocalPresentation(BaseGUI ignoredPresentation)
        {
            if (DialogueSync.Instance?.IsInSyncedDialogue != true)
                return;

            var playerCharacter = MainGame.me?.player_char;
            if (playerCharacter == null || !playerCharacter.control_enabled)
                return;

            if (SpeechBubbleGUI.all != null && SpeechBubbleGUI.all.Count > 0)
                return;

            if (BaseGUI.opened_windows != null)
            {
                foreach (BaseGUI gui in BaseGUI.opened_windows)
                {
                    if (gui != null && gui != ignoredPresentation && gui.is_shown)
                        return;
                }
            }

            DialogueSync.Instance.NotifyDialogueEnd();
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Local presentation callback completed the shared dialogue");
        }

        private static bool IsExpectedRemotePeer(CSteamID senderID)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            return onlineCoop != null &&
                   onlineCoop.IsOnlineCoopEnabled &&
                   onlineCoop.IsRemotePlayer(senderID);
        }

        private void ResetPresentationState()
        {
            sharedTechPopupOpen = false;
            sharedTutorialOpen = false;
            sharedTechGateAdvanced = false;
            sharedTutorialGateAdvanced = false;
            lastSharedTechCloseFrame = -1;
            lastSharedTutorialCloseFrame = -1;
            suppressLastSharedTechClose = false;
            suppressLastSharedTutorialClose = false;
            finishDialogueAfterLocalTutorialHide = false;
            applyingRemoteTechPresentation = false;
            applyingRemoteTutorialPresentation = false;
            remoteTechPresentationActive = false;
            sharedTechAggregatesUnlocks = false;
            techPresentationAggregationUntil = 0f;
            sharedTechId = string.Empty;
            sharedTechReveal = false;
            sharedTechPseudo = false;
        }

        private static List<string> ReadStringList(BinaryReader reader)
        {
            int count = reader.ReadUInt16();
            var list = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(reader.ReadString());
            }
            return list;
        }

        private static List<int> ReadIntList(BinaryReader reader)
        {
            int count = reader.ReadUInt16();
            var list = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(reader.ReadInt32());
            }
            return list;
        }

        private void SetGameLogicsBlackList(GameLogics gameLogics, List<string> newList)
        {
            if (gameLogics == null) return;
            try
            {
                if (gameLogicsBlackListField == null)
                {
                    gameLogicsBlackListField = typeof(GameLogics).GetField("_black_list",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }
                if (gameLogicsBlackListField != null)
                {
                    gameLogicsBlackListField.SetValue(gameLogics, newList ?? new List<string>());
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Failed to set GameLogics blacklist: {ex.Message}");
            }
        }

        private void RefreshTechDependentState()
        {
            try
            {
                var allObjects = WGORegistry.Instance?.SnapshotAll() ?? MainGame.me?.GetListOfWorldObjects();
                if (allObjects != null)
                {
                    for (int i = 0; i < allObjects.Count; i++)
                    {
                        var wgo = allObjects[i];
                        if (wgo?.components?.craft != null)
                        {
                            wgo.components.craft.FillCraftsList();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogDebug($"{LogPrefix} Error refreshing craft lists: {ex.Message}");
            }
        }
    }
}

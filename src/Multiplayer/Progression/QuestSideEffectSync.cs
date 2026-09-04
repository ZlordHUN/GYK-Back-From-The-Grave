using System;
using System.Collections.Generic;
using GraveyardKeeperCoop.Network;
using UnityEngine;

namespace GraveyardKeeperCoop.Multiplayer
{
    internal static class QuestSideEffectSync
    {
        private const float PersonalRewardSourceConverted = 1f;
        private const float PersonalRewardSourceLegacyAmbiguous = 2f;
        private const float PersonalRewardSourceSanitized = 3f;
        private static readonly HashSet<string> AppliedThisSession = new HashSet<string>();

        public static void ResetSession()
        {
            AppliedThisSession.Clear();
        }

        /// <summary>
        /// Reconcile durable observer effects for quests that are already complete.
        /// This covers late join, profile restore, and saves made before a personal
        /// reward receipt existed.
        /// </summary>
        public static void ReconcileSucceededQuestEffects()
        {
            QuestSystem quests = MainGame.me?.save?.quests;
            if (quests == null)
                return;

            string[] questIds =
                StoryCatalogRuntime.GetQuestIdsWithObserverSuccessEffects();
            for (int i = 0; i < questIds.Length; i++)
            {
                if (quests.IsQuestSucced(questIds[i]))
                {
                    bool hasPersonalRewardSource =
                        StoryCatalogRuntime.GetPersonalRewardSource(
                            questIds[i]) != null;
                    ApplySucceededQuestParameterRepair(questIds[i]);
                    ApplySucceededQuestPersonalItemGrants(
                        questIds[i],
                        hasPersonalRewardSource);
                    ReconcilePersonalRewardSourceContainer(questIds[i]);
                    ApplySucceededQuestGlobalScripts(questIds[i]);
                }
            }
        }

        public static void ReconcileCurrentQuestEffects()
        {
            QuestSystem quests = MainGame.me?.save?.quests;
            if (quests == null)
                return;

            string[] questIds =
                StoryCatalogRuntime.GetQuestIdsWithObserverStartEffects();
            for (int i = 0; i < questIds.Length; i++)
            {
                if (quests.IsQuestCurrent(questIds[i]))
                    ApplyObserverStartEffects(questIds[i]);
            }
        }

        public static void ReconcileSucceededQuestParameters()
        {
            ReconcileSucceededQuestEffects();
        }

        public static void ApplySucceededQuestParameterRepair(string questId)
        {
            ApplyDurableEffects(
                StoryCatalogRuntime.GetObserverSuccessEffects(questId),
                $"succeeded quest '{questId}'");
        }

        public static void OnLocalQuestStarted(string questId)
        {
            ApplyObserverStartEffects(questId);
        }

        public static void OnRemoteQuestStarted(string questId)
        {
            ApplyObserverStartEffects(questId);
        }

        private static void ApplyObserverStartEffects(string questId)
        {
            QuestSystem quests = MainGame.me?.save?.quests;
            if (quests == null || !quests.IsQuestCurrent(questId) ||
                quests.IsQuestSucced(questId) || quests.IsQuestFaild(questId))
            {
                return;
            }

            ApplyDurableEffects(
                StoryCatalogRuntime.GetObserverStartEffects(questId),
                $"active quest '{questId}'");
        }

        private static bool SetPlayerParam(
            WorldGameObject player,
            string paramName,
            float value)
        {
            if (player?.data == null || string.IsNullOrEmpty(paramName))
                return false;

            float current = player.data.GetParam(paramName, 0f);
            if (Mathf.Abs(current - value) < 0.0001f)
            {
                return false;
            }

            player.data.SetParam(paramName, value);
            if (string.Equals(
                    paramName,
                    "do_not_show_wgo_qualities",
                    StringComparison.Ordinal))
            {
                MainGame.me?.player_component?.CheckShowWGOQuality();
            }
            return true;
        }

        internal static void ApplyTaskMilestoneDurableEffects(
            StoryCatalogTaskMilestone milestone)
        {
            if (milestone == null)
                return;

            ApplyDurableEffects(
                milestone.playerParamRepairs,
                milestone.playerParamMinimums,
                milestone.taskStateMinimums,
                milestone.unlockedTechs,
                milestone.unlockedPhrases,
                milestone.blacklistedPhrases,
                milestone.unlockedCrafts,
                milestone.completedOneTimeCrafts,
                $"task milestone '{milestone.milestoneId}'");
        }

        private static void ApplyDurableEffects(
            StoryCatalogObserverSuccess effects,
            string source)
        {
            if (effects == null)
                return;

            ApplyDurableEffects(
                effects.playerParamRepairs,
                effects.playerParamMinimums,
                effects.taskStateMinimums,
                effects.unlockedTechs,
                effects.unlockedPhrases,
                effects.blacklistedPhrases,
                effects.unlockedCrafts,
                effects.completedOneTimeCrafts,
                source);
        }

        private static void ApplyDurableEffects(
            StoryCatalogPlayerParamRepair[] repairs,
            StoryCatalogPlayerParamRepair[] minimums,
            StoryCatalogTaskStateRepair[] taskMinimums,
            string[] unlockedTechs,
            string[] unlockedPhrases,
            string[] blacklistedPhrases,
            string[] unlockedCrafts,
            string[] completedOneTimeCrafts,
            string source)
        {
            WorldGameObject player = MainGame.me?.player;
            GameSave save = MainGame.me?.save;
            if (player?.data == null || save == null)
                return;

            int changedParams = 0;
            repairs = repairs ?? new StoryCatalogPlayerParamRepair[0];
            for (int i = 0; i < repairs.Length; i++)
            {
                StoryCatalogPlayerParamRepair repair = repairs[i];
                if (ShouldSkipRepair(repair))
                    continue;
                if (SetPlayerParam(player, repair.key, repair.value))
                    changedParams++;
            }

            minimums = minimums ?? new StoryCatalogPlayerParamRepair[0];
            for (int i = 0; i < minimums.Length; i++)
            {
                StoryCatalogPlayerParamRepair minimum = minimums[i];
                if (ShouldSkipRepair(minimum) ||
                    player.data.GetParam(minimum.key, 0f) >= minimum.value)
                {
                    continue;
                }
                if (SetPlayerParam(player, minimum.key, minimum.value))
                    changedParams++;
            }

            int changedTasks = ApplyTaskStateMinimums(save, taskMinimums);
            int changedUnlocks = ApplyUnlocks(
                save,
                unlockedTechs,
                unlockedPhrases,
                blacklistedPhrases,
                unlockedCrafts,
                completedOneTimeCrafts);

            if (changedParams > 0)
                PlayerParamSync.Instance?.MarkDirty();
            if (changedTasks > 0)
                QuestSync.Instance?.MarkDirty();
            if (changedUnlocks > 0)
                TechSync.Instance?.MarkDirty();
            if (changedParams + changedTasks + changedUnlocks == 0)
                return;

            CoopMod.Logger.LogInfo(
                $"[QuestSideEffectSync] Reconciled {source}: " +
                $"params={changedParams}, tasks={changedTasks}, " +
                $"unlocks={changedUnlocks}");
        }

        private static bool ShouldSkipRepair(StoryCatalogPlayerParamRepair repair)
        {
            if (repair == null)
                return true;

            QuestSystem quests = MainGame.me?.save?.quests;
            string[] blockers = repair.unlessQuestSucceeded ?? new string[0];
            for (int i = 0; i < blockers.Length; i++)
            {
                if (!StoryCatalogRuntime.IsKnownQuestId(blockers[i]))
                    return true;
                if (quests?.IsQuestSucced(blockers[i]) == true)
                    return true;
            }

            string[] reachedBlockers = repair.unlessQuestReached ??
                new string[0];
            for (int i = 0; i < reachedBlockers.Length; i++)
            {
                string blocker = reachedBlockers[i];
                if (!StoryCatalogRuntime.IsKnownQuestId(blocker))
                    return true;
                if (quests != null &&
                    (quests.IsQuestCurrent(blocker) ||
                     quests.IsQuestSucced(blocker) ||
                     quests.IsQuestFaild(blocker)))
                {
                    return true;
                }
            }
            return false;
        }

        private static int ApplyTaskStateMinimums(
            GameSave save,
            StoryCatalogTaskStateRepair[] repairs)
        {
            if (save?.known_npcs == null || repairs == null)
                return 0;

            int changed = 0;
            for (int i = 0; i < repairs.Length; i++)
            {
                StoryCatalogTaskStateRepair repair = repairs[i];
                string npcId = StoryCatalogRuntime.ResolveKnownNpcId(repair.npcId);
                KnownNPC npc = save.known_npcs.GetOrCreateNPC(npcId);
                KnownNPC.TaskState.State target =
                    string.Equals(repair.state, "Complete", StringComparison.Ordinal)
                        ? KnownNPC.TaskState.State.Complete
                        : KnownNPC.TaskState.State.Visible;
                KnownNPC.TaskState.State current = npc.GetQuestState(repair.taskId);
                if (current == KnownNPC.TaskState.State.Complete || current == target)
                    continue;

                npc.SetQuestState(repair.taskId, target);
                changed++;
            }

            if (changed > 0)
            {
                GUIElements.me?.relation?.npc_tasks?.Redraw();
            }
            return changed;
        }

        private static int ApplyUnlocks(
            GameSave save,
            string[] unlockedTechs,
            string[] unlockedPhrases,
            string[] blacklistedPhrases,
            string[] unlockedCrafts,
            string[] completedOneTimeCrafts)
        {
            int changed = 0;
            unlockedTechs = unlockedTechs ?? new string[0];
            for (int i = 0; i < unlockedTechs.Length; i++)
            {
                if (save.unlocked_techs.Contains(unlockedTechs[i]))
                    continue;
                save.UnlockTech(unlockedTechs[i]);
                changed++;
            }

            unlockedPhrases = unlockedPhrases ?? new string[0];
            for (int i = 0; i < unlockedPhrases.Length; i++)
            {
                if (save.unlocked_phrases.Contains(unlockedPhrases[i]))
                    continue;
                save.UnlockPhrase(unlockedPhrases[i]);
                changed++;
            }

            blacklistedPhrases = blacklistedPhrases ?? new string[0];
            for (int i = 0; i < blacklistedPhrases.Length; i++)
            {
                if (save.black_list_of_phrases.Contains(blacklistedPhrases[i]))
                    continue;
                save.AddPhraseToBlackList(blacklistedPhrases[i]);
                changed++;
            }

            unlockedCrafts = unlockedCrafts ?? new string[0];
            for (int i = 0; i < unlockedCrafts.Length; i++)
            {
                if (save.unlocked_crafts.Contains(unlockedCrafts[i]))
                    continue;
                save.UnlockCraft(unlockedCrafts[i]);
                changed++;
            }

            completedOneTimeCrafts = completedOneTimeCrafts ?? new string[0];
            for (int i = 0; i < completedOneTimeCrafts.Length; i++)
            {
                if (save.completed_one_time_crafts.Contains(
                        completedOneTimeCrafts[i]))
                {
                    continue;
                }
                save.completed_one_time_crafts.Add(completedOneTimeCrafts[i]);
                changed++;
            }
            return changed;
        }

        public static void ApplyNewSucceededQuests(HashSet<string> previousSucceeded, List<string> currentSucceeded)
        {
            if (currentSucceeded == null)
                return;

            for (int i = 0; i < currentSucceeded.Count; i++)
            {
                string questId = currentSucceeded[i];
                if (previousSucceeded != null && previousSucceeded.Contains(questId))
                    continue;

                OnRemoteQuestSucceeded(questId);
            }
        }

        public static void OnLocalQuestSucceeded(string questId)
        {
            ApplySucceededQuestParameterRepair(questId);
            if (StoryCatalogRuntime.GetPersonalRewardSource(questId) != null)
                ApplySucceededQuestPersonalItemGrants(questId, true);
            else
                RecordSucceededQuestPersonalItemGrantReceipts(questId);
            ReconcilePersonalRewardSourceContainer(questId);
        }

        public static void OnRemoteQuestSucceeded(string questId)
        {
            ApplySucceededQuestParameterRepair(questId);
            ApplySucceededQuestPersonalItemGrants(questId, true);
            ReconcilePersonalRewardSourceContainer(questId);

            ApplySucceededQuestGlobalScripts(questId);
        }

        private static void ApplySucceededQuestGlobalScripts(string questId)
        {

            if (string.IsNullOrEmpty(questId))
                return;

            string[] scripts =
                StoryCatalogRuntime.GetObserverSuccessScripts(questId);
            if (scripts.Length == 0 || !AppliedThisSession.Add(questId))
                return;

            for (int i = 0; i < scripts.Length; i++)
            {
                try
                {
                    RunGlobalFlowScript(scripts[i]);
                }
                catch (Exception ex)
                {
                    CoopMod.Logger.LogWarning($"[QuestSideEffectSync] Failed to run '{scripts[i]}' for quest '{questId}': {ex.Message}");
                }
            }
        }

        public static bool IsPersonalGrantReceipt(string paramName)
        {
            return StoryCatalogRuntime.IsPersonalGrantReceiptId(paramName);
        }

        public static List<string> CapturePersonalGrantReceipts()
        {
            var receipts = new List<string>();
            Item data = MainGame.me?.player?.data;
            if (data == null)
                return receipts;

            string[] receiptIds =
                StoryCatalogRuntime.GetPersonalGrantReceiptIds();
            for (int i = 0; i < receiptIds.Length; i++)
            {
                if (data.GetParam(receiptIds[i], 0f) >= 0.5f)
                    receipts.Add(receiptIds[i]);
            }
            return receipts;
        }

        public static void ClearPersonalGrantReceipts()
        {
            Item data = MainGame.me?.player?.data;
            if (data == null)
                return;

            string[] receiptIds =
                StoryCatalogRuntime.GetPersonalGrantReceiptIds();
            for (int i = 0; i < receiptIds.Length; i++)
                data.SetParam(receiptIds[i], 0f);
        }

        public static void ApplyPersonalGrantReceipts(
            IEnumerable<string> receiptIds)
        {
            Item data = MainGame.me?.player?.data;
            if (data == null || receiptIds == null)
                return;

            foreach (string receiptId in receiptIds)
            {
                if (IsPersonalGrantReceipt(receiptId))
                    data.SetParam(receiptId, 1f);
            }
        }

        /// <summary>
        /// Older profiles contain authoritative personal inventory but either no
        /// story-grant receipts or no receipts for task-based grants. Treat
        /// rewards from completed story state as received so consumed or sold
        /// items are not recreated during reconciliation.
        /// </summary>
        public static int AdoptLegacyStoryGrantReceipts()
        {
            QuestSystem quests = MainGame.me?.save?.quests;
            Item data = MainGame.me?.player?.data;
            if (quests == null || data == null)
                return 0;
            if (data.GetParam(
                    StoryCatalogRuntime.LegacyReceiptAdoptionId,
                    0f) >= 0.5f)
            {
                return 0;
            }

            int adopted = 0;
            string[] questIds =
                StoryCatalogRuntime.GetQuestIdsWithObserverSuccessEffects();
            for (int i = 0; i < questIds.Length; i++)
            {
                if (!quests.IsQuestSucced(questIds[i]))
                    continue;

                StoryCatalogPersonalItemGrant[] grants =
                    StoryCatalogRuntime.GetObserverSuccessPersonalItemGrants(
                        questIds[i]);
                for (int j = 0; j < grants.Length; j++)
                {
                    StoryCatalogPersonalItemGrant grant = grants[j];
                    if (grant == null ||
                        data.GetParam(grant.receiptId, 0f) >= 0.5f)
                    {
                        continue;
                    }

                    data.SetParam(grant.receiptId, 1f);
                    adopted++;
                }
            }

            KnownNPCList knownNpcs = MainGame.me?.save?.known_npcs;
            StoryCatalogTaskMilestone[] milestones =
                StoryCatalogRuntime.GetTaskMilestones();
            for (int i = 0; i < milestones.Length; i++)
            {
                StoryCatalogTaskMilestone milestone = milestones[i];
                string knownNpcId =
                    StoryCatalogRuntime.ResolveKnownNpcId(milestone.npcId);
                KnownNPC npc = knownNpcs?.GetNPC(knownNpcId);
                if (npc == null || !StoryCatalogRuntime.IsTaskMilestoneActive(
                        milestone,
                        npc.GetQuestState(milestone.taskId)))
                {
                    continue;
                }

                StoryCatalogPersonalItemGrant[] grants =
                    milestone.personalItemGrants ??
                    new StoryCatalogPersonalItemGrant[0];
                for (int j = 0; j < grants.Length; j++)
                {
                    StoryCatalogPersonalItemGrant grant = grants[j];
                    if (grant == null ||
                        data.GetParam(grant.receiptId, 0f) >= 0.5f)
                    {
                        continue;
                    }

                    data.SetParam(grant.receiptId, 1f);
                    adopted++;
                }
            }

            data.SetParam(StoryCatalogRuntime.LegacyReceiptAdoptionId, 1f);
            PlayerParamSync.Instance?.MarkDirty();
            if (adopted > 0)
            {
                CoopMod.Logger.LogInfo(
                    $"[QuestSideEffectSync] Adopted {adopted} completed-story " +
                    "grant receipt(s) for a legacy personal profile without " +
                    "recreating consumed items");
            }
            return adopted;
        }

        public static void EnsureLocalCutscenePersonalItemGrantAllocation(
            string scriptName)
        {
            if (string.IsNullOrEmpty(scriptName))
                return;

            ApplyPersonalItemGrants(
                StoryCatalogRuntime.GetCutscenePersonalItemGrants(scriptName),
                $"authoritative cutscene '{scriptName}'",
                grantFullObserverAllocation: false);
        }

        public static void ApplyRemoteCutscenePersonalItemGrants(
            string scriptName)
        {
            if (string.IsNullOrEmpty(scriptName))
                return;

            ApplyPersonalItemGrants(
                StoryCatalogRuntime.GetCutscenePersonalItemGrants(scriptName),
                $"observed cutscene '{scriptName}'",
                grantFullObserverAllocation: true);
        }

        /// <summary>
        /// Recover cutscene rewards for late joiners and profiles created before
        /// cutscene receipts existed. A present unreceipted item is treated as the
        /// vanilla allocation; an absent item is delivered once. Once receipted,
        /// later consumption never recreates it.
        /// </summary>
        public static void ReconcileCompletedCutscenePersonalItemGrants()
        {
            StoryCatalogCutscene[] cutscenes =
                StoryCatalogRuntime.GetCutscenesWithPersonalItemGrants();
            for (int i = 0; i < cutscenes.Length; i++)
            {
                StoryCatalogCutscene cutscene = cutscenes[i];
                if (!IsCutscenePersonalGrantCompletionReached(cutscene))
                    continue;

                string[] scripts = cutscene.triggerScripts ?? new string[0];
                bool active = false;
                for (int j = 0; j < scripts.Length; j++)
                {
                    if (Patches.CutsceneSyncPatches.IsCutsceneSessionActive(
                            scripts[j]))
                    {
                        active = true;
                        break;
                    }
                }
                if (active)
                    continue;

                ApplyPersonalItemGrants(
                    cutscene.personalItemGrants,
                    $"completed cutscene '{cutscene.cutsceneId}'",
                    grantFullObserverAllocation: false);
            }
        }

        private static bool IsCutscenePersonalGrantCompletionReached(
            StoryCatalogCutscene cutscene)
        {
            QuestSystem quests = MainGame.me?.save?.quests;
            Item data = MainGame.me?.player?.data;
            if (cutscene == null || quests == null || data == null)
                return false;

            string[] prerequisites =
                cutscene.personalGrantPrerequisiteQuestIds ?? new string[0];
            if (prerequisites.Length == 0)
                return false;
            for (int i = 0; i < prerequisites.Length; i++)
            {
                if (!quests.IsQuestSucced(prerequisites[i]))
                    return false;
            }

            StoryCatalogStateCheck[] checks = cutscene.completionChecks ??
                new StoryCatalogStateCheck[0];
            if (checks.Length == 0)
                return false;
            for (int i = 0; i < checks.Length; i++)
            {
                StoryCatalogStateCheck check = checks[i];
                if (check == null ||
                    !string.Equals(
                        check.source,
                        "player_param",
                        StringComparison.Ordinal))
                {
                    return false;
                }

                float actual = data.GetParam(check.key, 0f);
                if (!CompareStoryState(
                        actual,
                        check.comparison,
                        check.value))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool CompareStoryState(
            float actual,
            string comparison,
            float expected)
        {
            switch (comparison)
            {
                case ">=": return actual >= expected;
                case ">": return actual > expected;
                case "<=": return actual <= expected;
                case "<": return actual < expected;
                case "==": return Mathf.Abs(actual - expected) < 0.0001f;
                default: return false;
            }
        }

        public static void ApplyTaskMilestonePersonalItemGrants(
            StoryCatalogTaskMilestone milestone,
            bool grantFullObserverAllocation,
            bool recordReceiptsOnly)
        {
            if (milestone == null)
                return;

            StoryCatalogPersonalItemGrant[] grants =
                milestone.personalItemGrants ??
                new StoryCatalogPersonalItemGrant[0];
            string source = $"task milestone '{milestone.milestoneId}'";
            if (recordReceiptsOnly)
                RecordPersonalItemGrantReceipts(grants, source);
            else
                ApplyPersonalItemGrants(
                    grants,
                    source,
                    grantFullObserverAllocation);
        }

        private static void RecordSucceededQuestPersonalItemGrantReceipts(
            string questId)
        {
            if (string.IsNullOrEmpty(questId))
                return;

            RecordPersonalItemGrantReceipts(
                StoryCatalogRuntime.GetObserverSuccessPersonalItemGrants(
                    questId),
                $"quest '{questId}'");
        }

        private static void RecordPersonalItemGrantReceipts(
            StoryCatalogPersonalItemGrant[] grants,
            string source)
        {
            Item data = MainGame.me?.player?.data;
            if (data == null || grants == null)
                return;

            int recorded = 0;
            for (int i = 0; i < grants.Length; i++)
            {
                StoryCatalogPersonalItemGrant grant = grants[i];
                if (grant == null ||
                    data.GetParam(grant.receiptId, 0f) >= 0.5f)
                {
                    continue;
                }

                data.SetParam(grant.receiptId, 1f);
                recorded++;
            }

            if (recorded == 0)
                return;

            PlayerParamSync.Instance?.MarkDirty();
            CoopMod.Logger.LogInfo(
                $"[QuestSideEffectSync] Recorded {recorded} vanilla personal " +
                $"grant receipt(s) for {source}");
        }

        private static void ApplySucceededQuestPersonalItemGrants(
            string questId,
            bool grantFullObserverAllocation)
        {
            OnlineCoopManager coop = OnlineCoopManager.Instance;
            WorldGameObject player = MainGame.me?.player;
            if (coop == null || !coop.IsOnlineCoopEnabled ||
                player?.data == null || string.IsNullOrEmpty(questId))
            {
                return;
            }

            StoryCatalogPersonalItemGrant[] grants =
                StoryCatalogRuntime.GetObserverSuccessPersonalItemGrants(
                    questId);
            ApplyPersonalItemGrants(
                grants,
                $"quest '{questId}'",
                grantFullObserverAllocation);
        }

        private static void ApplyPersonalItemGrants(
            StoryCatalogPersonalItemGrant[] grants,
            string source,
            bool grantFullObserverAllocation)
        {
            OnlineCoopManager coop = OnlineCoopManager.Instance;
            WorldGameObject player = MainGame.me?.player;
            if (coop == null || !coop.IsOnlineCoopEnabled ||
                player?.data == null || grants == null)
            {
                return;
            }

            for (int i = 0; i < grants.Length; i++)
            {
                StoryCatalogPersonalItemGrant grant = grants[i];
                if (grant == null)
                {
                    continue;
                }

                int current = player.data.GetTotalCount(grant.itemId, true);
                bool hasReceipt =
                    player.data.GetParam(grant.receiptId, 0f) >= 0.5f;
                if (hasReceipt)
                    continue;

                int required = grantFullObserverAllocation
                    ? current + grant.count
                    : Math.Max(current, grant.count);
                int missing = required - current;
                if (missing > 0)
                {
                    player.AddToInventory(grant.itemId, missing);
                    current = player.data.GetTotalCount(grant.itemId, true);
                }

                if (current < required)
                {
                    CoopMod.Logger.LogWarning(
                        $"[QuestSideEffectSync] Could not deliver personal " +
                        $"prologue grant '{grant.itemId}' x{grant.count} for " +
                        $"{source}; current={current}, " +
                        $"required={required}. It will retry.");
                    continue;
                }

                player.data.SetParam(grant.receiptId, 1f);
                PlayerParamSync.Instance?.MarkDirty();
                JoinerProfileManager.NotifyPersonalInventoryChanged(
                    $"story grant {source}:{grant.itemId}");
                CoopMod.Logger.LogInfo(
                    $"[QuestSideEffectSync] Confirmed personal grant " +
                    $"'{grant.itemId}' x{grant.count} for {source} " +
                    $"receipt='{grant.receiptId}'");
            }
        }

        internal static bool TryPreparePersonalRewardContainerForMove(
            WorldGameObject chest,
            out string reason)
        {
            reason = string.Empty;
            if (chest?.data == null ||
                !StoryCatalogRuntime.TryGetPersonalRewardSourceForContainer(
                    chest.custom_tag,
                    out string questId,
                    out StoryCatalogPersonalRewardSource source,
                    out StoryCatalogPersonalItemGrant[] grants))
            {
                return true;
            }

            if (IsPersonalRewardSourceResolved(chest, source))
                return true;

            OnlineCoopManager coop = OnlineCoopManager.Instance;
            if (coop == null || !coop.IsOnlineCoopEnabled || !coop.IsHost)
            {
                reason = "waiting for the host to convert the personal reward source";
                return false;
            }

            QuestSystem quests = MainGame.me?.save?.quests;
            if (quests == null || !quests.IsQuestSucced(questId))
            {
                reason = "personal reward quest has not completed on the host";
                return false;
            }

            ResolvePersonalRewardSourceContainer(
                questId,
                chest,
                source,
                grants);
            reason = IsPersonalRewardSourceResolved(chest, source)
                ? "personal reward source was converted; retry the move"
                : "personal reward source conversion is not ready";

            // Never let the move that triggered conversion continue with a stale
            // selected Item reference. The canonical redraw makes a retry safe.
            return false;
        }

        private static void ReconcilePersonalRewardSourceContainer(string questId)
        {
            OnlineCoopManager coop = OnlineCoopManager.Instance;
            StoryCatalogPersonalRewardSource source =
                StoryCatalogRuntime.GetPersonalRewardSource(questId);
            if (source == null || coop == null || !coop.IsOnlineCoopEnabled ||
                !coop.IsHost)
            {
                return;
            }

            QuestSystem quests = MainGame.me?.save?.quests;
            if (quests == null || !quests.IsQuestSucced(questId))
                return;

            WorldGameObject chest = FindPersonalRewardSourceContainer(source);
            if (chest?.data == null ||
                IsPersonalRewardSourceResolved(chest, source))
            {
                return;
            }

            ResolvePersonalRewardSourceContainer(
                questId,
                chest,
                source,
                StoryCatalogRuntime.GetObserverSuccessPersonalItemGrants(
                    questId));
        }

        internal static void ReconcilePersonalRewardSourceAfterRemoteQuestState(
            string questId)
        {
            // QuestSync deliberately applies observer side effects before it writes
            // the terminal quest state. Retry only the source conversion after that
            // state is durable so a client-initiated grave-tools quest cannot leave
            // the host's chest permanently unresolved.
            ReconcilePersonalRewardSourceContainer(questId);
        }

        private static WorldGameObject FindPersonalRewardSourceContainer(
            StoryCatalogPersonalRewardSource source)
        {
            if (source == null || string.IsNullOrEmpty(source.containerCustomTag))
                return null;

            try
            {
                WorldGameObject byTag =
                    WorldMap.GetWorldGameObjectByCustomTag(
                        source.containerCustomTag,
                        true);
                if (byTag != null)
                    return byTag;
            }
            catch
            {
                // The registry fallback below also covers zones that are still
                // completing their load when profile reconciliation runs.
            }

            List<WorldGameObject> objects =
                WGORegistry.Instance?.SnapshotAll() ??
                MainGame.me?.GetListOfWorldObjects();
            if (objects == null)
                return null;

            for (int i = 0; i < objects.Count; i++)
            {
                WorldGameObject candidate = objects[i];
                if (candidate != null &&
                    string.Equals(
                        candidate.custom_tag,
                        source.containerCustomTag,
                        StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static bool IsPersonalRewardSourceResolved(
            WorldGameObject chest,
            StoryCatalogPersonalRewardSource source)
        {
            if (chest?.data == null || source == null)
                return false;

            float state = chest.data.GetParam(source.conversionMarker, 0f);
            return state >= 0.5f &&
                   Mathf.Abs(
                       state - PersonalRewardSourceLegacyAmbiguous) >= 0.0001f;
        }

        private static void ResolvePersonalRewardSourceContainer(
            string questId,
            WorldGameObject chest,
            StoryCatalogPersonalRewardSource source,
            StoryCatalogPersonalItemGrant[] grants)
        {
            if (chest?.data == null || source == null ||
                IsPersonalRewardSourceResolved(chest, source) ||
                !string.Equals(
                    source.conversionMode,
                    StoryCatalogRuntime.ConsumeExactUntouchedSeed,
                    StringComparison.Ordinal))
            {
                return;
            }

            float previousResolution = chest.data.GetParam(
                source.conversionMarker,
                0f);
            bool migratingLegacyAmbiguous = Mathf.Abs(
                previousResolution -
                PersonalRewardSourceLegacyAmbiguous) < 0.0001f;
            bool exactSeed = IsExactUntouchedSeed(chest.data, grants);
            bool empty = IsItemListEmpty(chest.data.inventory) &&
                         IsItemListEmpty(chest.data.secondary_inventory);
            int sanitizedSeedCount = 0;
            if (exactSeed)
            {
                sanitizedSeedCount = GetCatalogGrantItemCount(grants);
                chest.data.inventory.Clear();
            }
            else if (!empty)
                sanitizedSeedCount = RemovePristineCatalogSeedItems(
                    chest.data,
                    grants);

            // Version 1 used state 2 for a mixed/partial source and then treated it
            // as terminal. That left every recognizable seed item withdrawable in
            // addition to the receipt-backed grants. State 3 records the one-time
            // conservative migration: remove at most the catalogued quantity of
            // pristine, top-level seed items while retaining unrelated, modified,
            // nested, and surplus contents as player property.
            float resolution = exactSeed || empty
                ? (migratingLegacyAmbiguous
                    ? PersonalRewardSourceSanitized
                    : PersonalRewardSourceConverted)
                : PersonalRewardSourceSanitized;
            chest.data.SetParam(source.conversionMarker, resolution);

            try
            {
                chest.Redraw(true);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogDebug(
                    $"[QuestSideEffectSync] Could not redraw personal reward " +
                    $"source '{source.containerCustomTag}': {ex.Message}");
            }

            InventorySync.Instance?.PublishAuthoritativeContainerState(
                chest,
                $"personal reward source for quest '{questId}'");

            if (migratingLegacyAmbiguous)
            {
                CoopMod.Logger.LogInfo(
                    $"[QuestSideEffectSync] Migrated legacy ambiguous personal " +
                    $"reward source '{source.containerCustomTag}' for quest " +
                    $"'{questId}'; removed {sanitizedSeedCount} recognizable " +
                    "catalog seed item(s) and preserved all other contents");
            }
            else if (exactSeed)
            {
                CoopMod.Logger.LogInfo(
                    $"[QuestSideEffectSync] Converted exact untouched container " +
                    $"seed '{source.containerCustomTag}' into receipt-backed " +
                    $"personal grants for quest '{questId}'");
            }
            else if (empty)
            {
                CoopMod.Logger.LogInfo(
                    $"[QuestSideEffectSync] Marked empty legacy personal reward " +
                    $"source '{source.containerCustomTag}' resolved for quest " +
                    $"'{questId}'");
            }
            else
            {
                CoopMod.Logger.LogInfo(
                    $"[QuestSideEffectSync] Sanitized mixed personal reward " +
                    $"source '{source.containerCustomTag}' for " +
                    $"quest '{questId}'; removed {sanitizedSeedCount} recognizable " +
                    "catalog seed item(s) and preserved all other contents");
            }
        }

        private static int GetCatalogGrantItemCount(
            StoryCatalogPersonalItemGrant[] grants)
        {
            if (grants == null)
                return 0;

            long total = 0L;
            for (int i = 0; i < grants.Length; i++)
            {
                if (grants[i] != null && grants[i].count > 0)
                    total += grants[i].count;
            }
            return total >= int.MaxValue ? int.MaxValue : (int)total;
        }

        private static int RemovePristineCatalogSeedItems(
            Item container,
            StoryCatalogPersonalItemGrant[] grants)
        {
            if (container?.inventory == null || grants == null ||
                grants.Length == 0)
            {
                return 0;
            }

            var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < grants.Length; i++)
            {
                StoryCatalogPersonalItemGrant grant = grants[i];
                if (grant == null || string.IsNullOrEmpty(grant.itemId) ||
                    grant.count <= 0)
                {
                    continue;
                }

                remaining.TryGetValue(grant.itemId, out int current);
                remaining[grant.itemId] = current + grant.count;
            }

            int removed = 0;
            for (int i = container.inventory.Count - 1; i >= 0; i--)
            {
                Item item = container.inventory[i];
                if (item == null || item.IsEmpty() ||
                    !remaining.TryGetValue(item.id, out int count) ||
                    count <= 0 || !IsUntouchedSeedItem(item))
                {
                    continue;
                }

                int take = Math.Min(item.value, count);
                if (take <= 0)
                    continue;

                if (take >= item.value)
                    container.inventory.RemoveAt(i);
                else
                    item.value -= take;

                remaining[item.id] = count - take;
                removed += take;
            }

            return removed;
        }

        private static bool IsExactUntouchedSeed(
            Item container,
            StoryCatalogPersonalItemGrant[] grants)
        {
            if (container == null || grants == null || grants.Length == 0 ||
                !IsItemListEmpty(container.secondary_inventory))
            {
                return false;
            }

            var expected = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < grants.Length; i++)
            {
                StoryCatalogPersonalItemGrant grant = grants[i];
                if (grant == null || string.IsNullOrEmpty(grant.itemId) ||
                    grant.count <= 0)
                {
                    return false;
                }

                expected.TryGetValue(grant.itemId, out int current);
                expected[grant.itemId] = current + grant.count;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            int nonEmptyCount = 0;
            List<Item> items = container.inventory;
            if (items == null)
                return false;

            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (item == null || item.IsEmpty())
                    continue;

                nonEmptyCount++;
                if (!expected.TryGetValue(item.id, out int expectedCount) ||
                    !seen.Add(item.id) || item.value != expectedCount ||
                    !IsUntouchedSeedItem(item))
                {
                    return false;
                }
            }

            return nonEmptyCount == expected.Count && seen.Count == expected.Count;
        }

        private static bool IsUntouchedSeedItem(Item item)
        {
            if (item == null || item.IsEmpty() || item.is_unique ||
                item.linked_id != -1 || item.worker_unique_id != -1L ||
                item.equipped_as != ItemDefinition.EquipmentType.None ||
                !string.IsNullOrEmpty(item.drop_zone_id) ||
                !string.IsNullOrEmpty(item.sub_name) ||
                Mathf.Abs(item.durability - 1f) > 0.0001f ||
                Mathf.Abs(item.hp) > 0.0001f ||
                Mathf.Abs(item.progress) > 0.0001f ||
                Mathf.Abs(item.money) > 0.0001f ||
                (item.multiquality_items != null &&
                 item.multiquality_items.Count > 0) ||
                !IsItemListEmpty(item.inventory) ||
                !IsItemListEmpty(item.secondary_inventory))
            {
                return false;
            }

            GameRes itemParams = item.GetParams();
            return itemParams == null || itemParams.Types.Count == 0;
        }

        private static bool IsItemListEmpty(List<Item> items)
        {
            if (items == null)
                return true;

            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] != null && !items[i].IsEmpty())
                    return false;
            }

            return true;
        }

        private static void RunGlobalFlowScript(string scriptName)
        {
            if (MainGame.me == null)
            {
                CoopMod.Logger.LogWarning($"[QuestSideEffectSync] Cannot run '{scriptName}' - game unavailable");
                return;
            }

            CustomFlowScript script = GS.RunFlowScript(scriptName, null);
            if (script == null)
            {
                CoopMod.Logger.LogWarning(
                    $"[QuestSideEffectSync] Global FlowScript '{scriptName}' was not found");
                return;
            }

            CoopMod.Logger.LogInfo($"[QuestSideEffectSync] Ran global FlowScript '{scriptName}'");
        }
    }
}

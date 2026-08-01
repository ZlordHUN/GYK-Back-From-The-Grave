using System;
using System.Collections.Generic;
using UnityEngine;

namespace GraveyardKeeperCoop.Multiplayer
{
    internal static class QuestSideEffectSync
    {
        private const string GraveToolsQuestId = "take_tools_from_grave_chest";
        private const string GraveToolsPlayerParam = "take_tools_from_grave_chest";
        private const string FirstBurialQuestId = "skull_talk_after_burial";
        private const string MorgueEntryLockParam = "tp_to_morgue_locked";
        private const string MorgueExitBodyLockParam =
            "tp_from_morgue_only_with_body";

        private static readonly Dictionary<string, string[]> ScriptsByQuest = new Dictionary<string, string[]>
        {
            { "go_to_talk_with_donkey_first_time", new[] { "unlock_tps" } }
        };

        private static readonly HashSet<string> AppliedThisSession = new HashSet<string>();

        public static void ResetSession()
        {
            AppliedThisSession.Clear();
        }

        /// <summary>
        /// Repair player parameters whose vanilla quest-end FlowScripts
        /// are intentionally not replayed on observing peers.
        /// </summary>
        public static void ReconcileSucceededQuestParameters()
        {
            QuestSystem quests = MainGame.me?.save?.quests;
            if (quests == null)
                return;

            if (quests.IsQuestSucced(GraveToolsQuestId))
                ApplySucceededQuestParameterRepair(GraveToolsQuestId);
            if (quests.IsQuestSucced(FirstBurialQuestId))
                ApplySucceededQuestParameterRepair(FirstBurialQuestId);
        }

        public static void ApplySucceededQuestParameterRepair(string questId)
        {
            WorldGameObject player = MainGame.me?.player;
            if (player?.data == null)
                return;

            bool changed;
            string description;
            if (string.Equals(questId, GraveToolsQuestId, StringComparison.Ordinal))
            {
                // A value of 1 means the Bishop should keep saying "Check the
                // trunk." Opening it completes the quest and clears the reminder.
                changed = ClearPlayerFlag(player, GraveToolsPlayerParam);
                description = "grave-tools dialogue reminder";
            }
            else if (string.Equals(questId, FirstBurialQuestId, StringComparison.Ordinal))
            {
                // on_after_first_bureal clears both restrictions before its live
                // presentation. Observing peers do not execute that FlowScript.
                bool entryChanged = ClearPlayerFlag(player, MorgueEntryLockParam);
                bool exitChanged = ClearPlayerFlag(player, MorgueExitBodyLockParam);
                changed = entryChanged || exitChanged;
                description = "first-burial morgue restrictions";
            }
            else
            {
                return;
            }

            if (!changed)
                return;

            PlayerParamSync.Instance?.MarkDirty();
            CoopMod.Logger.LogInfo(
                $"[QuestSideEffectSync] Cleared {description} " +
                $"from succeeded quest '{questId}'");
        }

        private static bool ClearPlayerFlag(
            WorldGameObject player,
            string paramName)
        {
            if (player?.data == null ||
                player.data.GetParam(paramName, 0f) < 0.5f)
            {
                return false;
            }

            player.data.SetParam(paramName, 0f);
            return true;
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

        public static void OnRemoteQuestSucceeded(string questId)
        {
            ApplySucceededQuestParameterRepair(questId);

            if (string.IsNullOrEmpty(questId) ||
                !ScriptsByQuest.TryGetValue(questId, out string[] scripts) ||
                scripts == null ||
                !AppliedThisSession.Add(questId))
            {
                return;
            }

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

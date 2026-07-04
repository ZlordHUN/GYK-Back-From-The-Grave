using System;
using System.Collections.Generic;
using UnityEngine;

namespace GraveyardKeeperCoop.Multiplayer
{
    internal static class QuestSideEffectSync
    {
        private static readonly Dictionary<string, string[]> ScriptsByQuest = new Dictionary<string, string[]>
        {
            { "go_to_talk_with_donkey_first_time", new[] { "unlock_tps" } }
        };

        private static readonly HashSet<string> AppliedThisSession = new HashSet<string>();

        public static void ResetSession()
        {
            AppliedThisSession.Clear();
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

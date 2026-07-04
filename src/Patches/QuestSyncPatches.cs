using HarmonyLib;
using System;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch]
    internal static class QuestSyncPatches
    {
        private static void MarkDirty()
        {
            var sync = GraveyardKeeperCoop.Multiplayer.QuestSync.Instance;
            if (sync == null) return;
            sync.MarkDirty();
        }

        [HarmonyPatch(typeof(GameSave), "OnMetNPC")]
        [HarmonyPrefix]
        internal static void OnMetNPC_Prefix(string npc_id)
        {
            var sync = GraveyardKeeperCoop.Multiplayer.QuestSync.Instance;
            if (sync == null) return;
            sync.SendMetNpcEvent(npc_id);
            MarkDirty();
        }

        [HarmonyPatch(typeof(GameSave), "SetTaskState")]
        [HarmonyPrefix]
        internal static void SetTaskState_Prefix(string npc_id, string task_id, KnownNPC.TaskState.State state, Action on_finished)
        {
            var sync = GraveyardKeeperCoop.Multiplayer.QuestSync.Instance;
            if (sync == null) return;
            sync.SendTaskStateEvent(npc_id, task_id, (int)state);
            MarkDirty();
        }

        [HarmonyPatch(typeof(KnownNPCList), "RemoveNPC")]
        [HarmonyPrefix]
        internal static void RemoveNPC_Prefix(string npc_id)
        {
            var sync = GraveyardKeeperCoop.Multiplayer.QuestSync.Instance;
            if (sync == null) return;
            sync.SendRemoveNpcEvent(npc_id);
            MarkDirty();
        }

        [HarmonyPatch(typeof(QuestSystem), "StartQuest")]
        [HarmonyPostfix]
        internal static void StartQuest_Postfix(QuestDefinition quest)
        {
            if (quest == null) return;
            var sync = GraveyardKeeperCoop.Multiplayer.QuestSync.Instance;
            if (sync == null) return;
            sync.SendQuestStartEvent(quest.id);
            MarkDirty();
        }

        [HarmonyPatch(typeof(QuestSystem), "EndQuest")]
        [HarmonyPostfix]
        internal static void EndQuest_Postfix(QuestState __0)
        {
            QuestState q_to_end = __0;
            if (q_to_end?.definition == null) return;
            var sync = GraveyardKeeperCoop.Multiplayer.QuestSync.Instance;
            if (sync == null) return;
            sync.SendQuestEndEvent(
                q_to_end.definition.id,
                q_to_end.state == QuestState.State.Succeeded);
            MarkDirty();
        }
    }
}

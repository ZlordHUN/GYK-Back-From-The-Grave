using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;
using GraveyardKeeperCoop.Patches;

namespace GraveyardKeeperCoop.Multiplayer
{
    public class QuestSync : PeriodicSyncBehaviour
    {
        public static QuestSync Instance => GetInstance<QuestSync>();

        private const byte PayloadVersion = 1;
        private const int MaxNpcs = 256;
        private const int MaxTasksPerNpc = 128;
        private const int MaxQuests = 256;
        private const int MaxStringsPerList = 4096;
        private const string GerryNpcId = "crafting_skull_3";
        private const string GerryBeerTaskId = "skull_beer";
        private const float GerryIntroductionRelationship = 10f;

        internal const byte SubFullSync = 0;
        internal const byte SubTaskState = 1;
        internal const byte SubMetNpc = 2;
        internal const byte SubRemoveNpc = 3;
        internal const byte SubQuestStart = 4;
        internal const byte SubQuestEnd = 5;

        protected override string LogPrefix => "[QuestSync]";
        protected override float SyncIntervalSeconds => 10f;

        protected override void OnPeriodicSyncEnabled()
        {
            QuestSideEffectSync.ResetSession();
            QuestSideEffectSync.ReconcileSucceededQuestParameters();
            ReconcileCurrentTaskRelationshipMilestones();
        }

        protected override void OnSyncDisabled()
        {
            QuestSideEffectSync.ResetSession();
        }

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnQuestSyncReceived -= OnQuestSyncReceived;
                SteamP2PManager.Instance.OnQuestSyncReceived += OnQuestSyncReceived;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnQuestSyncReceived -= OnQuestSyncReceived;
            }
        }

        protected override bool ShouldUpdate() =>
            MainGame.me != null && MainGame.game_started && MainGame.me.save != null;

        protected override bool ShouldSend() =>
            MainGame.me != null && MainGame.game_started && MainGame.me.save != null;

        internal static void ApplyTaskRelationshipMilestone(
            string npcId,
            string taskId,
            KnownNPC.TaskState.State state)
        {
            var coop = OnlineCoopManager.Instance;
            if (coop == null || !coop.IsOnlineCoopEnabled ||
                state != KnownNPC.TaskState.State.Visible ||
                !string.Equals(npcId, GerryNpcId, StringComparison.Ordinal) ||
                !string.Equals(taskId, GerryBeerTaskId, StringComparison.Ordinal))
            {
                return;
            }

            WorldGameObject player = MainGame.me?.player;
            if (player?.data == null)
                return;

            string relationshipKey = "_rel_" + GerryNpcId;
            float current = player.data.GetParam(relationshipKey, 0f);
            if (current >= GerryIntroductionRelationship)
                return;

            player.SetParam(relationshipKey, GerryIntroductionRelationship);
            CoopMod.Logger.LogInfo(
                $"[QuestSync] Applied Gerry introduction relationship milestone: " +
                $"{current:F0} -> {GerryIntroductionRelationship:F0}");
        }

        private static void ReconcileCurrentTaskRelationshipMilestones()
        {
            KnownNPC gerry = MainGame.me?.save?.known_npcs?.GetNPC(GerryNpcId);
            if (gerry == null)
                return;

            KnownNPC.TaskState.State taskState =
                gerry.GetQuestState(GerryBeerTaskId);
            if (taskState == KnownNPC.TaskState.State.Visible ||
                taskState == KnownNPC.TaskState.State.Complete)
            {
                ApplyTaskRelationshipMilestone(
                    GerryNpcId,
                    GerryBeerTaskId,
                    KnownNPC.TaskState.State.Visible);
            }
        }


        internal void SendTaskStateEvent(string npcId, string taskId, int state)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (IsApplyingRemoteChange) return;

            using (var stream = new MemoryStream(256))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubTaskState);
                bw.Write(NextSequence++);
                bw.Write(npcId ?? "");
                bw.Write(taskId ?? "");
                bw.Write((byte)state);
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastQuestSync(stream.ToArray());
            }
            CoopMod.Logger.LogInfo($"{LogPrefix} Sent TaskState: npc={npcId}, task={taskId}, state={state}");
        }

        internal void SendMetNpcEvent(string npcId)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (IsApplyingRemoteChange) return;

            using (var stream = new MemoryStream(128))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubMetNpc);
                bw.Write(NextSequence++);
                bw.Write(npcId ?? "");
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastQuestSync(stream.ToArray());
            }
            CoopMod.Logger.LogInfo($"{LogPrefix} Sent MetNpc: npc={npcId}");
        }

        internal void SendRemoveNpcEvent(string npcId)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (IsApplyingRemoteChange) return;

            using (var stream = new MemoryStream(128))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubRemoveNpc);
                bw.Write(NextSequence++);
                bw.Write(npcId ?? "");
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastQuestSync(stream.ToArray());
            }
            CoopMod.Logger.LogInfo($"{LogPrefix} Sent RemoveNpc: npc={npcId}");
        }

        internal void SendQuestStartEvent(string questId)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (IsApplyingRemoteChange) return;

            using (var stream = new MemoryStream(128))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubQuestStart);
                bw.Write(NextSequence++);
                bw.Write(questId ?? "");
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastQuestSync(stream.ToArray());
            }
            CoopMod.Logger.LogInfo($"{LogPrefix} Sent QuestStart: quest={questId}");
        }

        internal void SendQuestEndEvent(string questId, bool succeeded)
        {
            if (!IsSyncEnabled) return;
            if (!IsOnline) return;
            if (IsApplyingRemoteChange) return;

            if (succeeded)
                QuestSideEffectSync.ApplySucceededQuestParameterRepair(questId);

            using (var stream = new MemoryStream(128))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubQuestEnd);
                bw.Write(NextSequence++);
                bw.Write(questId ?? "");
                bw.Write(succeeded);
                bw.Flush();
                SteamP2PManager.Instance?.BroadcastQuestSync(stream.ToArray());
            }
            CoopMod.Logger.LogInfo($"{LogPrefix} Sent QuestEnd: quest={questId}, succeeded={succeeded}");
        }

        protected override byte[] CaptureSnapshot(out string fingerprint)
        {
            var sb = new StringBuilder();
            var save = MainGame.me.save;
            var quests = save.quests;

            using (var stream = new MemoryStream(8192))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubFullSync);
                bw.Write(NextSequence++);

                WriteNpcList(bw, save.known_npcs, sb);
                WriteQuestStateList(bw, quests, sb);
                WriteStringList(bw, GetSucceededQuests(quests), sb);
                WriteStringList(bw, GetFailedQuests(quests), sb);
                WriteStringList(bw, GetExecutedQuests(quests), sb);

                bw.Flush();
                fingerprint = sb.ToString();
                return stream.ToArray();
            }
        }

        protected override void BroadcastPayload(byte[] payload)
        {
            SteamP2PManager.Instance?.BroadcastQuestSync(payload);
        }

        private static List<string> GetSucceededQuests(QuestSystem qs)
        {
            var field = typeof(QuestSystem).GetField("_succed_quests",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            return field?.GetValue(qs) as List<string> ?? new List<string>();
        }

        private static List<string> GetFailedQuests(QuestSystem qs)
        {
            var field = typeof(QuestSystem).GetField("_failed_quests",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            return field?.GetValue(qs) as List<string> ?? new List<string>();
        }

        private static List<string> GetExecutedQuests(QuestSystem qs)
        {
            var field = typeof(QuestSystem).GetField("_executed_quests",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            return field?.GetValue(qs) as List<string> ?? new List<string>();
        }

        private static void WriteNpcList(BinaryWriter bw, KnownNPCList knownNpcs, StringBuilder sb)
        {
            if (knownNpcs == null || knownNpcs.npcs == null)
            {
                bw.Write((ushort)0);
                return;
            }
            int count = Mathf.Min(knownNpcs.npcs.Count, MaxNpcs);
            bw.Write((ushort)count);
            sb.Append("npcs:").Append(count).Append(';');
            for (int i = 0; i < count; i++)
            {
                var npc = knownNpcs.npcs[i];
                bw.Write(npc.npc_id ?? "");
                if (npc.tasks == null)
                {
                    bw.Write((ushort)0);
                }
                else
                {
                    int taskCount = Mathf.Min(npc.tasks.Count, MaxTasksPerNpc);
                    bw.Write((ushort)taskCount);
                    for (int j = 0; j < taskCount; j++)
                    {
                        bw.Write(npc.tasks[j].id ?? "");
                        bw.Write((byte)npc.tasks[j].state);
                    }
                }
            }
        }

        private static void WriteQuestStateList(BinaryWriter bw, QuestSystem quests, StringBuilder sb)
        {
            var currentQuests = quests?.GetCurrentQuests();
            if (currentQuests == null)
            {
                bw.Write((ushort)0);
                return;
            }

            var synchronizedQuests = new List<QuestState>();
            for (int i = 0;
                 i < currentQuests.Count && synchronizedQuests.Count < MaxQuests;
                 i++)
            {
                var qs = currentQuests[i];
                if (qs?.definition == null)
                {
                    continue;
                }

                synchronizedQuests.Add(qs);
            }

            bw.Write((ushort)synchronizedQuests.Count);
            sb.Append("q:").Append(synchronizedQuests.Count).Append(';');
            for (int i = 0; i < synchronizedQuests.Count; i++)
            {
                var qs = synchronizedQuests[i];
                bw.Write(qs.definition?.id ?? "");
                bw.Write((byte)qs.state);
                bw.Write(qs.start_time);
            }
        }

        private static void WriteStringList(BinaryWriter bw, List<string> list, StringBuilder sb)
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

        private void OnQuestSyncReceived(CSteamID senderID, byte[] payload)
        {
            TryReadPayloadWithSubtype(payload, PayloadVersion, (reader, subType) =>
            {
                switch (subType)
                {
                    case SubFullSync:
                        HandleFullSync(reader, senderID);
                        break;
                    case SubTaskState:
                        HandleTaskState(reader, senderID);
                        break;
                    case SubMetNpc:
                        HandleMetNpc(reader, senderID);
                        break;
                    case SubRemoveNpc:
                        HandleRemoveNpc(reader, senderID);
                        break;
                    case SubQuestStart:
                        HandleQuestStart(reader, senderID);
                        break;
                    case SubQuestEnd:
                        HandleQuestEnd(reader, senderID);
                        break;
                    default:
                        CoopMod.Logger.LogWarning($"{LogPrefix} Unknown sub-type {subType}");
                        break;
                }
            });
        }

        private void HandleFullSync(BinaryReader reader, CSteamID senderID)
        {
            if (MainGame.me == null || MainGame.me.save == null) return;
            if (!CheckSequence(reader, senderID)) return;

            var save = MainGame.me.save;

            try
            {
                var previouslySucceeded = save.quests != null
                    ? new HashSet<string>(GetSucceededQuests(save.quests))
                    : new HashSet<string>();

                ApplyNpcList(reader, save);
                ReconcileCurrentTaskRelationshipMilestones();
                ApplyQuestStateList(reader, save);
                ApplyStringList(reader, ref save.quests, "_succed_quests");
                if (save.quests != null)
                    QuestSideEffectSync.ApplyNewSucceededQuests(previouslySucceeded, GetSucceededQuests(save.quests));
                ApplyStringList(reader, ref save.quests, "_failed_quests");
                ApplyStringList(reader, ref save.quests, "_executed_quests");

                ApplyEchoSuppress();
                ForceNextSend = true;
                RefreshQuestPresentation();

                CoopMod.Logger.LogInfo($"{LogPrefix} Applied full quest/NPC knowledge snapshot");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Error applying full sync: {ex.Message}");
            }
        }

        private void ApplyNpcList(BinaryReader reader, GameSave save)
        {
            int npcCount = reader.ReadUInt16();
            if (save.known_npcs == null) save.known_npcs = new KnownNPCList();
            save.known_npcs.npcs.Clear();
            for (int i = 0; i < npcCount; i++)
            {
                string npcId = reader.ReadString();
                int taskCount = reader.ReadUInt16();
                var npc = new KnownNPC { npc_id = npcId };
                for (int j = 0; j < taskCount; j++)
                {
                    string taskId = reader.ReadString();
                    byte stateByte = reader.ReadByte();
                    npc.tasks.Add(new KnownNPC.TaskState
                    {
                        id = taskId,
                        state = (KnownNPC.TaskState.State)stateByte
                    });
                }
                save.known_npcs.npcs.Add(npc);
            }
        }

        private void ApplyQuestStateList(BinaryReader reader, GameSave save)
        {
            int count = reader.ReadUInt16();
            if (save.quests == null) return;
            var currentQuests = save.quests.GetCurrentQuests();
            if (currentQuests == null) return;

            var questDefs = GameBalance.me?.quests_data;
            for (int i = 0; i < count; i++)
            {
                string questId = reader.ReadString();
                byte stateByte = reader.ReadByte();
                long startTime = reader.ReadInt64();
                QuestDefinition questDef = null;
                if (questDefs != null)
                {
                    questDef = GameBalance.me.GetDataOrNull<QuestDefinition>(questId);
                }
                if (questDef == null) continue;
                if (save.quests.IsQuestSucced(questId) ||
                    save.quests.IsQuestFaild(questId))
                {
                    // A stale host snapshot must not resurrect a quest that this
                    // peer completed and has already reported to the host.
                    for (int j = currentQuests.Count - 1; j >= 0; j--)
                    {
                        if (currentQuests[j]?.definition?.id == questId)
                            currentQuests.RemoveAt(j);
                    }
                    continue;
                }

                bool exists = false;
                for (int j = 0; j < currentQuests.Count; j++)
                {
                    if (currentQuests[j].definition == questDef)
                    {
                        currentQuests[j].state = (QuestState.State)stateByte;
                        currentQuests[j].start_time = startTime;
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                {
                    currentQuests.Add(new QuestState
                    {
                        definition = questDef,
                        state = (QuestState.State)stateByte,
                        start_time = startTime
                    });
                    // Initialize completion triggers without replaying the quest's
                    // introductory FlowScript on this peer.
                    questDef.InitQuestEndTriggers();
                }
            }
        }

        private void ApplyStringList(BinaryReader reader, ref QuestSystem quests, string fieldName)
        {
            int count = reader.ReadUInt16();
            var field = typeof(QuestSystem).GetField(fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (field == null) return;
            var list = field.GetValue(quests) as List<string>;
            if (list == null) return;
            list.Clear();
            for (int i = 0; i < count; i++)
            {
                list.Add(reader.ReadString());
            }
        }

        private void HandleTaskState(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            string npcId = reader.ReadString();
            string taskId = reader.ReadString();
            byte stateByte = reader.ReadByte();
            var state = (KnownNPC.TaskState.State)stateByte;

            if (MainGame.me?.save?.known_npcs != null)
            {
                ApplyRemote(() =>
                {
                    GameSave save = MainGame.me.save;
                    var npc = save.known_npcs.GetOrCreateNPC(npcId);
                    ApplyTaskRelationshipMilestone(npcId, taskId, state);
                    if (npc.GetQuestState(taskId) == state)
                        return;

                    // Commit immediately so shared progression never depends on a
                    // flying UI animation reaching its destination. Then run the
                    // vanilla presentation path: it opens the temporary NPC panel,
                    // animates the task marker and displays the relation progress
                    // that the initiating player sees. ApplyRemote keeps the
                    // GameSave.SetTaskState Harmony prefix from echoing this event.
                    npc.SetQuestState(taskId, state);
                    try
                    {
                        save.SetTaskState(npcId, taskId, state, null);
                    }
                    catch (Exception ex)
                    {
                        CoopMod.Logger.LogWarning(
                            $"{LogPrefix} Applied TaskState but could not show " +
                            $"its vanilla presentation: {ex.Message}");
                    }
                });
                CoopMod.Logger.LogInfo($"{LogPrefix} Applied TaskState: npc={npcId}, task={taskId}, state={state}");
            }
        }

        private void HandleMetNpc(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            string npcId = reader.ReadString();
            if (MainGame.me?.save?.known_npcs != null)
            {
                ApplyRemote(() => MainGame.me.save.known_npcs.GetOrCreateNPC(npcId));
                CoopMod.Logger.LogInfo($"{LogPrefix} Applied MetNpc: npc={npcId}");
            }
        }

        private void HandleRemoveNpc(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            string npcId = reader.ReadString();
            if (MainGame.me?.save?.known_npcs != null)
            {
                ApplyRemote(() => MainGame.me.save.known_npcs.RemoveNPC(npcId));
                CoopMod.Logger.LogInfo($"{LogPrefix} Applied RemoveNpc: npc={npcId}");
            }
        }

        private void HandleQuestStart(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            string questId = reader.ReadString();
            CutsceneSyncPatches.MarkRemoteFirstBurialCutsceneHandoff(
                questId);
            var questDef = GameBalance.me?.GetDataOrNull<QuestDefinition>(questId);
            if (questDef != null && MainGame.me?.save?.quests != null)
            {
                var currentQuests = MainGame.me.save.quests.GetCurrentQuests();
                bool alreadyActive = false;
                if (currentQuests != null)
                {
                    for (int i = 0; i < currentQuests.Count; i++)
                    {
                        if (currentQuests[i].definition == questDef)
                        {
                            alreadyActive = true;
                            break;
                        }
                    }
                }

                if (alreadyActive)
                {
                    CoopMod.Logger.LogInfo($"{LogPrefix} Skipped QuestStart: quest={questId} (already active locally)");
                    return;
                }

                ApplyRemote(() => ApplyRemoteQuestStartState(questDef));
                CoopMod.Logger.LogInfo($"{LogPrefix} Applied QuestStart: quest={questId}");
            }
        }

        private static void ApplyRemoteQuestStartState(QuestDefinition questDef)
        {
            QuestSystem quests = MainGame.me?.save?.quests;
            List<QuestState> currentQuests = quests?.GetCurrentQuests();
            if (quests == null || currentQuests == null || questDef == null)
                return;

            currentQuests.Add(new QuestState
            {
                definition = questDef,
                state = QuestState.State.InProgress
            });

            // QuestSystem.StartQuest also calls StartStartingScripts. The initiating
            // peer owns that live cutscene, so synchronize only durable quest state
            // and the triggers needed for either peer to complete it later. The
            // active QuestState also drives QuestListGUI's golden objective arrow.
            questDef.InitQuestEndTriggers();
            List<string> executedQuests = GetExecutedQuests(quests);
            if (!executedQuests.Contains(questDef.id))
                executedQuests.Add(questDef.id);

            GUIElements.me?.quest_list?.Redraw();
        }

        private void HandleQuestEnd(BinaryReader reader, CSteamID senderID)
        {
            if (!CheckSequence(reader, senderID)) return;

            string questId = reader.ReadString();
            bool succeeded = reader.ReadBoolean();
            if (MainGame.me?.save?.quests != null)
            {
                QuestSystem quests = MainGame.me.save.quests;
                if (succeeded)
                {
                    ApplyRemote(() =>
                        QuestSideEffectSync.ApplySucceededQuestParameterRepair(questId));
                }

                if ((succeeded && quests.IsQuestSucced(questId)) ||
                    (!succeeded && quests.IsQuestFaild(questId)))
                {
                    CoopMod.Logger.LogInfo(
                        $"{LogPrefix} Skipped QuestEnd: quest={questId} " +
                        "(already completed locally)");
                    return;
                }

                ApplyRemote(() =>
                    ApplyRemoteQuestEndState(
                        quests,
                        questId,
                        succeeded));
                CoopMod.Logger.LogInfo($"{LogPrefix} Applied QuestEnd: quest={questId}, succeeded={succeeded}");
            }
        }

        private static void ApplyRemoteQuestEndState(
            QuestSystem quests,
            string questId,
            bool succeeded)
        {
            if (quests == null || string.IsNullOrEmpty(questId))
                return;

            List<QuestState> currentQuests = quests.GetCurrentQuests();
            if (currentQuests != null)
            {
                for (int i = currentQuests.Count - 1; i >= 0; i--)
                {
                    if (currentQuests[i]?.definition?.id == questId)
                        currentQuests.RemoveAt(i);
                }
            }

            List<string> completed = succeeded
                ? GetSucceededQuests(quests)
                : GetFailedQuests(quests);
            if (!completed.Contains(questId))
                completed.Add(questId);

            // The initiating peer owns the quest's live success/failure script.
            // Other sync systems carry its durable rewards and presentations;
            // replaying EndQuest here starts a second cutscene and can close the
            // first peer's reward popup.
            if (succeeded)
                QuestSideEffectSync.OnRemoteQuestSucceeded(questId);

            GUIElements.me?.quest_list?.Redraw();
        }

        private static void RefreshQuestPresentation()
        {
            GUIElements.me?.quest_list?.Redraw();
            GUIElements.me?.relation?.npc_tasks?.Redraw();
        }
    }
}

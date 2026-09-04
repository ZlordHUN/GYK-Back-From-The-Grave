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
            StoryCatalogRuntime.ResetSession();
            QuestSideEffectSync.ResetSession();
            if (IsHost)
                QuestSideEffectSync.AdoptLegacyStoryGrantReceipts();
            QuestSideEffectSync.ReconcileCurrentQuestEffects();
            QuestSideEffectSync.ReconcileSucceededQuestEffects();
            ReconcileCurrentTaskMilestones();
            QuestSideEffectSync.ReconcileCompletedCutscenePersonalItemGrants();
        }

        protected override void OnSyncDisabled()
        {
            StoryCatalogRuntime.ResetSession();
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

        internal static void ApplyTaskMilestones(
            string npcId,
            string taskId,
            KnownNPC.TaskState.State state,
            bool grantFullPersonalAllocation = false,
            bool recordPersonalReceiptsOnly = false)
        {
            var coop = OnlineCoopManager.Instance;
            if (coop == null || !coop.IsOnlineCoopEnabled)
            {
                return;
            }

            WorldGameObject player = MainGame.me?.player;
            if (player?.data == null)
                return;

            StoryCatalogTaskMilestone[] milestones =
                StoryCatalogRuntime.GetTaskMilestones();
            string resolvedNpcId =
                StoryCatalogRuntime.ResolveKnownNpcId(npcId);
            for (int i = 0; i < milestones.Length; i++)
            {
                StoryCatalogTaskMilestone milestone = milestones[i];
                string resolvedMilestoneNpcId =
                    StoryCatalogRuntime.ResolveKnownNpcId(milestone.npcId);
                if (!string.Equals(
                        resolvedMilestoneNpcId,
                        resolvedNpcId,
                        StringComparison.Ordinal) ||
                    !string.Equals(milestone.taskId, taskId, StringComparison.Ordinal) ||
                    !StoryCatalogRuntime.IsTaskMilestoneActive(milestone, state))
                {
                    continue;
                }

                // The local SetTaskState prefix runs before the vanilla graph's
                // remaining nodes. In receipt-only mode, let that graph finish
                // its durable effects so an additive reward is not applied twice.
                if (!recordPersonalReceiptsOnly)
                {
                    QuestSideEffectSync.ApplyTaskMilestoneDurableEffects(milestone);

                    StoryCatalogWgoParamRepair[] wgoMinimums =
                        milestone.wgoParamMinimums ??
                        new StoryCatalogWgoParamRepair[0];
                    for (int j = 0; j < wgoMinimums.Length; j++)
                    {
                        StoryCatalogWgoParamRepair minimum = wgoMinimums[j];
                        WorldGameObject target = WorldMap.GetWorldGameObjectByObjId(
                            minimum.objectId,
                            true);
                        if (target?.data == null)
                            continue;

                        float current = target.GetParam(minimum.key, 0f);
                        if (current >= minimum.value)
                            continue;

                        target.SetParam(minimum.key, minimum.value);
                        CoopMod.Logger.LogInfo(
                            $"[QuestSync] Applied catalogued task milestone " +
                            $"'{milestone.milestoneId}' to '{minimum.objectId}': " +
                            $"{minimum.key} {current:F0} -> {minimum.value:F0}");
                    }
                }

                QuestSideEffectSync.ApplyTaskMilestonePersonalItemGrants(
                    milestone,
                    grantFullPersonalAllocation,
                    recordPersonalReceiptsOnly);
            }
        }

        internal static void ReconcileCurrentTaskMilestones()
        {
            KnownNPCList knownNpcs = MainGame.me?.save?.known_npcs;
            if (knownNpcs == null)
                return;

            StoryCatalogTaskMilestone[] milestones =
                StoryCatalogRuntime.GetTaskMilestones();
            var reconciledTasks = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < milestones.Length; i++)
            {
                StoryCatalogTaskMilestone milestone = milestones[i];
                string taskKey = milestone.npcId + "\n" + milestone.taskId;
                if (!reconciledTasks.Add(taskKey))
                    continue;

                string knownNpcId =
                    StoryCatalogRuntime.ResolveKnownNpcId(milestone.npcId);
                KnownNPC npc = knownNpcs.GetNPC(knownNpcId);
                if (npc == null)
                    continue;

                ApplyTaskMilestones(
                    knownNpcId,
                    milestone.taskId,
                    npc.GetQuestState(milestone.taskId));
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
            // Retry milestone repairs as zones and their world objects become
            // available. This also heals legacy saves whose task state is already
            // complete and therefore will not fire another SetTaskState event.
            ReconcileCurrentTaskMilestones();

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
                var previouslyFailed = save.quests != null
                    ? new HashSet<string>(GetFailedQuests(save.quests))
                    : new HashSet<string>();
                Dictionary<string, List<KnownNPC.TaskState>> previousTaskStates =
                    CaptureTaskStates(save.known_npcs);

                ApplyNpcList(reader, save);
                MergeTaskStates(save.known_npcs, previousTaskStates);
                ApplyQuestStateList(reader, save);
                ApplyStringList(reader, ref save.quests, "_succed_quests");
                ApplyStringList(reader, ref save.quests, "_failed_quests");
                ApplyStringList(reader, ref save.quests, "_executed_quests");
                MergeTerminalQuestStates(
                    save.quests,
                    previouslySucceeded,
                    previouslyFailed);
                RemoveTerminalCurrentQuests(save.quests);
                QuestSideEffectSync.ReconcileCurrentQuestEffects();
                if (save.quests != null)
                {
                    QuestSideEffectSync.ApplyNewSucceededQuests(
                        previouslySucceeded,
                        GetSucceededQuests(save.quests));
                }
                ReconcileCurrentTaskMilestones();
                QuestSideEffectSync.ReconcileCompletedCutscenePersonalItemGrants();

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

        private static Dictionary<string, List<KnownNPC.TaskState>> CaptureTaskStates(
            KnownNPCList knownNpcs)
        {
            var result = new Dictionary<string, List<KnownNPC.TaskState>>(
                StringComparer.Ordinal);
            if (knownNpcs?.npcs == null)
                return result;

            for (int i = 0; i < knownNpcs.npcs.Count; i++)
            {
                KnownNPC npc = knownNpcs.npcs[i];
                string npcId = StoryCatalogRuntime.ResolveKnownNpcId(npc?.npc_id);
                if (npc == null || string.IsNullOrEmpty(npcId) || npc.tasks == null)
                    continue;

                if (!result.TryGetValue(
                        npcId,
                        out List<KnownNPC.TaskState> captured))
                {
                    captured = new List<KnownNPC.TaskState>();
                    result.Add(npcId, captured);
                }

                MergeTaskList(captured, npc.tasks);
            }
            return result;
        }

        private static void MergeTaskStates(
            KnownNPCList knownNpcs,
            Dictionary<string, List<KnownNPC.TaskState>> previousTaskStates)
        {
            if (knownNpcs?.npcs == null || previousTaskStates == null)
                return;

            var incomingNpcIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < knownNpcs.npcs.Count; i++)
            {
                KnownNPC npc = knownNpcs.npcs[i];
                string npcId = StoryCatalogRuntime.ResolveKnownNpcId(npc?.npc_id);
                if (npc == null || string.IsNullOrEmpty(npcId) || npc.tasks == null)
                    continue;

                incomingNpcIds.Add(npcId);
                var merged = new List<KnownNPC.TaskState>();
                MergeTaskList(merged, npc.tasks);
                if (previousTaskStates.TryGetValue(
                        npcId,
                        out List<KnownNPC.TaskState> previous))
                {
                    // Preserve task progress only for NPCs present in the incoming
                    // snapshot. This closes crossed-event races without recreating
                    // an NPC that a later story action intentionally removed.
                    MergeTaskList(merged, previous);
                }

                npc.tasks.Clear();
                npc.tasks.AddRange(merged);
            }

            // A task event can cross an older host snapshot in the opposite
            // direction. Preserve an omitted NPC only when it owns a catalogued
            // story milestone; all other omissions remain authoritative so a
            // deliberate RemoveNPC action is not undone.
            HashSet<string> cataloguedTasks = GetCataloguedTaskKeys();
            foreach (KeyValuePair<string, List<KnownNPC.TaskState>> previous in
                     previousTaskStates)
            {
                if (incomingNpcIds.Contains(previous.Key))
                    continue;

                var preserved = new List<KnownNPC.TaskState>();
                for (int i = 0; i < previous.Value.Count; i++)
                {
                    KnownNPC.TaskState task = previous.Value[i];
                    if (task != null && cataloguedTasks.Contains(
                            GetTaskKey(previous.Key, task.id)))
                    {
                        MergeTaskList(
                            preserved,
                            new List<KnownNPC.TaskState> { task });
                    }
                }

                if (preserved.Count == 0)
                    continue;

                var restoredNpc = new KnownNPC { npc_id = previous.Key };
                restoredNpc.tasks.AddRange(preserved);
                knownNpcs.npcs.Add(restoredNpc);
                CoopMod.Logger.LogWarning(
                    $"[QuestSync] Preserved {preserved.Count} catalogued task " +
                    $"state(s) for omitted NPC '{previous.Key}' across a stale " +
                    "full snapshot");
            }
        }

        private static HashSet<string> GetCataloguedTaskKeys()
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            StoryCatalogTaskMilestone[] milestones =
                StoryCatalogRuntime.GetTaskMilestones();
            for (int i = 0; i < milestones.Length; i++)
            {
                StoryCatalogTaskMilestone milestone = milestones[i];
                result.Add(GetTaskKey(
                    StoryCatalogRuntime.ResolveKnownNpcId(milestone.npcId),
                    milestone.taskId));
            }
            return result;
        }

        private static string GetTaskKey(string npcId, string taskId) =>
            (npcId ?? string.Empty) + "\n" + (taskId ?? string.Empty);

        private static void MergeTaskList(
            List<KnownNPC.TaskState> destination,
            List<KnownNPC.TaskState> source)
        {
            for (int i = 0; i < source.Count; i++)
            {
                KnownNPC.TaskState candidate = source[i];
                if (candidate == null || string.IsNullOrEmpty(candidate.id) ||
                    candidate.state == KnownNPC.TaskState.State.Unknown)
                {
                    continue;
                }

                KnownNPC.TaskState existing = null;
                for (int j = 0; j < destination.Count; j++)
                {
                    if (string.Equals(
                            destination[j].id,
                            candidate.id,
                            StringComparison.Ordinal))
                    {
                        existing = destination[j];
                        break;
                    }
                }

                if (existing == null)
                {
                    destination.Add(new KnownNPC.TaskState
                    {
                        id = candidate.id,
                        state = candidate.state
                    });
                }
                else if (candidate.state == KnownNPC.TaskState.State.Complete)
                {
                    existing.state = KnownNPC.TaskState.State.Complete;
                }
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

        private static void RemoveTerminalCurrentQuests(QuestSystem quests)
        {
            List<QuestState> currentQuests = quests?.GetCurrentQuests();
            if (currentQuests == null)
                return;

            for (int i = currentQuests.Count - 1; i >= 0; i--)
            {
                string questId = currentQuests[i]?.definition?.id;
                if (!string.IsNullOrEmpty(questId) &&
                    (quests.IsQuestSucced(questId) ||
                     quests.IsQuestFaild(questId)))
                {
                    currentQuests.RemoveAt(i);
                }
            }
        }

        private static void MergeTerminalQuestStates(
            QuestSystem quests,
            HashSet<string> previouslySucceeded,
            HashSet<string> previouslyFailed)
        {
            if (quests == null)
                return;

            List<string> succeeded = GetSucceededQuests(quests);
            List<string> failed = GetFailedQuests(quests);
            HashSet<string> succeededSet = DeduplicateTerminalList(succeeded);
            HashSet<string> failedSet = DeduplicateTerminalList(failed);

            // Full snapshots and reliable quest-end events have independent sender
            // sequences. Preserve a terminal state already observed locally when an
            // older host snapshot crosses the completion event in flight.
            foreach (string questId in previouslySucceeded)
            {
                if (succeededSet.Add(questId))
                    succeeded.Add(questId);
            }

            foreach (string questId in previouslyFailed)
            {
                if (failedSet.Add(questId))
                    failed.Add(questId);
            }

            // A malformed or concurrently completed snapshot must not leave a quest
            // in both terminal lists. Success wins globally so every peer resolves
            // the same payload identically, regardless of its local history.
            for (int i = failed.Count - 1; i >= 0; i--)
            {
                string questId = failed[i];
                if (!succeededSet.Contains(questId))
                    continue;

                failed.RemoveAt(i);
                failedSet.Remove(questId);
                CoopMod.Logger.LogWarning(
                    $"[QuestSync] Resolved conflicting terminal quest state as " +
                    $"succeeded: quest={questId}");
            }
        }

        private static HashSet<string> DeduplicateTerminalList(List<string> list)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < list.Count;)
            {
                if (string.IsNullOrEmpty(list[i]) || !seen.Add(list[i]))
                    list.RemoveAt(i);
                else
                    i++;
            }
            return seen;
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
                    KnownNPC.TaskState.State current =
                        npc.GetQuestState(taskId);
                    if (GetTaskStateRank(current) > GetTaskStateRank(state))
                    {
                        ApplyTaskMilestones(
                            npcId,
                            taskId,
                            current,
                            grantFullPersonalAllocation: true);
                        CoopMod.Logger.LogWarning(
                            $"{LogPrefix} Ignored regressive TaskState: " +
                            $"npc={npcId}, task={taskId}, " +
                            $"current={current}, incoming={state}");
                        return;
                    }

                    ApplyTaskMilestones(
                        npcId,
                        taskId,
                        state,
                        grantFullPersonalAllocation: true);
                    if (current == state)
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

        private static int GetTaskStateRank(KnownNPC.TaskState.State state)
        {
            if (state == KnownNPC.TaskState.State.Complete)
                return 2;
            if (state == KnownNPC.TaskState.State.Visible)
                return 1;
            return 0;
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
            if (!StoryCatalogRuntime.ShouldApplyRemoteQuestAsDurableStateOnly(questId))
            {
                CoopMod.Logger.LogError(
                    $"{LogPrefix} Rejected unsupported observer application for " +
                    $"QuestStart: quest={questId}");
                return;
            }
            var questDef = GameBalance.me?.GetDataOrNull<QuestDefinition>(questId);
            QuestSystem quests = MainGame.me?.save?.quests;
            if (questDef != null && quests != null &&
                (quests.IsQuestSucced(questId) || quests.IsQuestFaild(questId)))
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Rejected QuestStart for terminal quest: " +
                    $"quest={questId}");
                return;
            }
            CutsceneSyncPatches.MarkRemoteFirstBurialCutsceneHandoff(
                questId);
            StoryCatalogRuntime.ObserveQuestStarted(questDef, remote: true);
            if (questDef != null && quests != null)
            {
                var currentQuests = quests.GetCurrentQuests();
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
                    ApplyRemote(() =>
                        QuestSideEffectSync.OnRemoteQuestStarted(questId));
                    CoopMod.Logger.LogInfo($"{LogPrefix} Skipped QuestStart: quest={questId} (already active locally)");
                    return;
                }

                ApplyRemote(() =>
                {
                    ApplyRemoteQuestStartState(questDef);
                    QuestSideEffectSync.OnRemoteQuestStarted(questId);
                });
                CoopMod.Logger.LogInfo($"{LogPrefix} Applied QuestStart: quest={questId}");
            }
        }

        private static void ApplyRemoteQuestStartState(QuestDefinition questDef)
        {
            QuestSystem quests = MainGame.me?.save?.quests;
            List<QuestState> currentQuests = quests?.GetCurrentQuests();
            if (quests == null || currentQuests == null || questDef == null)
                return;
            if (quests.IsQuestSucced(questDef.id) ||
                quests.IsQuestFaild(questDef.id))
            {
                return;
            }

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
            if (!StoryCatalogRuntime.ShouldApplyRemoteQuestAsDurableStateOnly(questId))
            {
                CoopMod.Logger.LogError(
                    $"{LogPrefix} Rejected unsupported observer application for " +
                    $"QuestEnd: quest={questId}");
                return;
            }
            StoryCatalogRuntime.ObserveQuestEnded(
                GameBalance.me?.GetDataOrNull<QuestDefinition>(questId),
                succeeded,
                remote: true);
            if (MainGame.me?.save?.quests != null)
            {
                QuestSystem quests = MainGame.me.save.quests;
                if (succeeded)
                {
                    ApplyRemote(() =>
                        QuestSideEffectSync.OnRemoteQuestSucceeded(questId));
                }

                if ((succeeded && quests.IsQuestSucced(questId)) ||
                    (!succeeded && quests.IsQuestFaild(questId)))
                {
                    NormalizeTerminalQuestStates(quests);
                    CoopMod.Logger.LogInfo(
                        $"{LogPrefix} Skipped QuestEnd: quest={questId} " +
                        "(already completed locally)");
                    return;
                }

                if (!succeeded && quests.IsQuestSucced(questId))
                {
                    NormalizeTerminalQuestStates(quests);
                    CoopMod.Logger.LogWarning(
                        $"{LogPrefix} Ignored conflicting failed QuestEnd because " +
                        $"success already wins locally: quest={questId}");
                    return;
                }

                ApplyRemote(() =>
                    ApplyRemoteQuestEndState(
                        quests,
                        questId,
                        succeeded));
                if (succeeded)
                {
                    ApplyRemote(() =>
                        QuestSideEffectSync
                            .ReconcilePersonalRewardSourceAfterRemoteQuestState(
                                questId));
                }
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
            NormalizeTerminalQuestStates(quests);

            // The initiating peer owns the quest's live success/failure script.
            // Other sync systems carry its durable rewards and presentations;
            // replaying EndQuest here starts a second cutscene and can close the
            // first peer's reward popup.
            GUIElements.me?.quest_list?.Redraw();
        }

        private static void NormalizeTerminalQuestStates(QuestSystem quests)
        {
            MergeTerminalQuestStates(
                quests,
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal));
        }

        private static void RefreshQuestPresentation()
        {
            GUIElements.me?.quest_list?.Redraw();
            GUIElements.me?.relation?.npc_tasks?.Redraw();
        }
    }
}

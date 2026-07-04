using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    public class CraftSync : PeriodicSyncBehaviour
    {
        public static CraftSync Instance => GetInstance<CraftSync>();

        private const byte PayloadVersion = 2;
        private const byte SubSnapshot = 0;
        private const byte SubCraftFinished = 1;
        private const int MaxPayloadBytes = 256 * 1024;
        private const int MaxQueueItems = 32;
        private const float LocalAuthorityReleaseSeconds = 3f;

        private readonly HashSet<long> dirtyWgoUniqueIds = new HashSet<long>();
        private readonly HashSet<long> locallyOwnedCrafts = new HashSet<long>();
        private readonly Dictionary<long, float> localAuthorityReleaseAt =
            new Dictionary<long, float>();
        private bool isApplyingRemoteCraftState;
        private bool isApplyingRemoteCraftCompletion;

        protected override string LogPrefix => "[CraftSync]";
        protected override float SyncIntervalSeconds => 2f;

        protected override void OnPeriodicSyncEnabled()
        {
            dirtyWgoUniqueIds.Clear();
            locallyOwnedCrafts.Clear();
            localAuthorityReleaseAt.Clear();
            isApplyingRemoteCraftState = false;
            isApplyingRemoteCraftCompletion = false;
        }

        protected override void OnSyncDisabled()
        {
            dirtyWgoUniqueIds.Clear();
            locallyOwnedCrafts.Clear();
            localAuthorityReleaseAt.Clear();
            isApplyingRemoteCraftState = false;
            isApplyingRemoteCraftCompletion = false;
        }

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnCraftSyncReceived -= OnCraftSyncReceived;
                SteamP2PManager.Instance.OnCraftSyncReceived += OnCraftSyncReceived;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnCraftSyncReceived -= OnCraftSyncReceived;
            }
        }

        protected override bool ShouldUpdate() =>
            MainGame.me != null && MainGame.game_started;

        protected override bool ShouldSend() =>
            MainGame.me != null && MainGame.game_started;

        protected override bool CanSendPeriodicSnapshot => true;

        internal bool IsApplyingRemoteCraftState =>
            isApplyingRemoteCraftState;

        internal void MarkDirty(long uniqueId)
        {
            if (!IsSyncEnabled ||
                !IsOnline ||
                isApplyingRemoteCraftState)
            {
                return;
            }
            dirtyWgoUniqueIds.Add(uniqueId);
            base.MarkDirty();
        }

        internal void NotifyLocalCraftStarted(CraftComponent craft)
        {
            if (isApplyingRemoteCraftState || craft?.wgo == null)
                return;
            if (!IsHost && craft.current_craft?.is_auto == true)
                return;

            long uniqueId = craft.wgo.unique_id;
            locallyOwnedCrafts.Add(uniqueId);
            localAuthorityReleaseAt.Remove(uniqueId);
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Local peer owns active craft '{craft.current_craft?.id ?? string.Empty}' " +
                $"on {craft.wgo.obj_id} (uid={uniqueId})");
        }

        internal void NotifyLocalCraftEnded(CraftComponent craft)
        {
            if (isApplyingRemoteCraftState || craft?.wgo == null)
                return;

            long uniqueId = craft.wgo.unique_id;
            if (!locallyOwnedCrafts.Remove(uniqueId))
                return;

            localAuthorityReleaseAt[uniqueId] =
                Time.realtimeSinceStartup + LocalAuthorityReleaseSeconds;
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Local craft authority released on {craft.wgo.obj_id} " +
                $"(uid={uniqueId}) after final state propagation");
        }

        internal void NotifyLocalCraftFinished(CraftDefinition craft)
        {
            if (!IsSyncEnabled ||
                !IsOnline ||
                IsHost ||
                isApplyingRemoteCraftCompletion ||
                craft == null ||
                string.IsNullOrEmpty(craft.id))
            {
                return;
            }

            using (var stream = new MemoryStream(128))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubCraftFinished);
                bw.Write(NextSequence++);
                bw.Write(craft.id);
                bw.Flush();
                SteamP2PManager.Instance?.SendCraftSyncToHost(
                    stream.ToArray());
            }

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Sent client craft completion: {craft.id}");
        }

        protected override byte[] CaptureSnapshot(out string fingerprint)
        {
            fingerprint = "";
            var objects = WGORegistry.Instance?.SnapshotAll() ?? MainGame.me?.GetListOfWorldObjects();
            if (objects == null) return null;

            var craftEntries = new List<CraftStateEntry>();
            var fingerprintBuilder = new StringBuilder();

            for (int i = 0; i < objects.Count; i++)
            {
                var wgo = objects[i];
                if (wgo == null) continue;

                var craft = wgo.components?.craft;
                if (craft == null) continue;

                bool hasActiveState = craft.is_crafting || wgo.progress > 0f;
                bool hasQueue = craft.craft_queue != null && craft.craft_queue.Count > 0;
                bool isDirty = dirtyWgoUniqueIds.Contains(wgo.unique_id);
                if (!hasActiveState && !hasQueue && !isDirty) continue;
                if (!IsHost &&
                    !ShouldPreserveLocalCraftState(wgo.unique_id))
                {
                    // Clients publish only crafts they initiated. Sending every
                    // active world craft made remotely spawned/automatic crafts
                    // compete with the host's canonical state.
                    continue;
                }

                var entry = CaptureEntry(wgo, craft);
                if (entry != null)
                {
                    craftEntries.Add(entry);
                    fingerprintBuilder.Append(entry.GetKey());
                }
            }

            dirtyWgoUniqueIds.Clear();

            if (craftEntries.Count == 0) return null;

            fingerprint = fingerprintBuilder.ToString();

            using (var stream = new MemoryStream(4096))
            using (var bw = new BinaryWriter(stream, Encoding.UTF8))
            {
                bw.Write(PayloadVersion);
                bw.Write(SubSnapshot);
                bw.Write(NextSequence++);

                bw.Write((ushort)craftEntries.Count);
                for (int i = 0; i < craftEntries.Count; i++)
                {
                    WriteEntry(bw, craftEntries[i]);
                }

                bw.Flush();
                return stream.ToArray();
            }
        }

        protected override void BroadcastPayload(byte[] payload)
        {
            if (IsHost)
            {
                SteamP2PManager.Instance?.BroadcastCraftSync(payload);
            }
            else
            {
                SteamP2PManager.Instance?.SendCraftSyncToHost(payload);
            }
        }

        private CraftStateEntry CaptureEntry(WorldGameObject wgo, CraftComponent craft)
        {
            try
            {
                var sc = craft.GetSerializedCraftComponent();

                var entry = new CraftStateEntry
                {
                    UniqueId = wgo.unique_id,
                    ObjId = wgo.obj_id ?? "",
                    CustomTag = wgo.custom_tag ?? "",
                    PosX = wgo.transform.position.x,
                    PosY = wgo.transform.position.y,
                    PosZ = wgo.transform.position.z,
                    Available = sc.available,
                    IsCrafting = sc.is_crafting,
                    CurrentCraftId = sc.cur_craft_id ?? "",
                    CraftAmount = sc.craft_amount,
                    Progress = wgo.progress,
                    CurItemId = sc.cur_item_id ?? "",
                    CurItemDur = sc.cur_item_dur,
                    DurItemId = sc.dur_item_id ?? "",
                    DurItemDur = sc.dur_item_dur,
                    MultiqualityItemId = sc.multiquality_item_id ?? "",
                    HasMultiqualityResult = sc.multiquality_craft_result != null,
                    MultiqualityResultValueItems = sc.multiquality_craft_result?.value_items ?? 0f,
                    MultiqualityResultValuePerks = sc.multiquality_craft_result?.value_perks ?? 0f,
                    MultiqualityResultValueDifficulty = sc.multiquality_craft_result?.value_difficulty ?? 0f,
                    MultiqualityResultQp1 = sc.multiquality_craft_result?.qp_1 ?? 0f,
                    MultiqualityResultQp2 = sc.multiquality_craft_result?.qp_2 ?? 0f,
                    MultiqualityResultQp3 = sc.multiquality_craft_result?.qp_3 ?? 0f,
                    LastCraftId = sc.last_craft_id ?? "",
                    LastCraftId2 = sc.last_craft_id_2 ?? "",
                    CurLastCraftSlot = sc.cur_last_craft_slot,
                    IsGratitudeSpent = sc.is_gratitude_points_spent_for_craft,
                    IsCurrentCraftGratitude = wgo.is_current_craft_gratitude,
                    WorkerIsPaused = craft.worker_is_paused
                };

                if (sc.queue != null && sc.queue.Count > 0)
                {
                    int count = Mathf.Min(sc.queue.Count, MaxQueueItems);
                    entry.Queue = new List<CraftQueueEntry>(count);
                    for (int i = 0; i < count; i++)
                    {
                        var qi = sc.queue[i];
                        entry.Queue.Add(new CraftQueueEntry
                        {
                            Id = qi.id ?? "",
                            N = qi.n,
                            Infinite = qi.infinite,
                            IsGratitudePointsCraft = qi.is_gratitude_points_craft
                        });
                    }
                }
                else
                {
                    entry.Queue = new List<CraftQueueEntry>(0);
                }

                return entry;
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Error capturing craft entry for {wgo.obj_id}: {ex.Message}");
                return null;
            }
        }

        private static void WriteEntry(BinaryWriter bw, CraftStateEntry entry)
        {
            bw.Write(entry.UniqueId);
            bw.Write(entry.ObjId);
            bw.Write(entry.CustomTag);
            bw.Write(entry.PosX);
            bw.Write(entry.PosY);
            bw.Write(entry.PosZ);
            bw.Write(entry.Available);
            bw.Write(entry.IsCrafting);
            bw.Write(entry.CurrentCraftId);
            bw.Write(entry.CraftAmount);
            bw.Write(entry.Progress);
            bw.Write(entry.CurItemId);
            bw.Write(entry.CurItemDur);
            bw.Write(entry.DurItemId);
            bw.Write(entry.DurItemDur);
            bw.Write(entry.MultiqualityItemId);
            bw.Write(entry.HasMultiqualityResult);
            if (entry.HasMultiqualityResult)
            {
                bw.Write(entry.MultiqualityResultValueItems);
                bw.Write(entry.MultiqualityResultValuePerks);
                bw.Write(entry.MultiqualityResultValueDifficulty);
                bw.Write(entry.MultiqualityResultQp1);
                bw.Write(entry.MultiqualityResultQp2);
                bw.Write(entry.MultiqualityResultQp3);
            }
            bw.Write(entry.LastCraftId);
            bw.Write(entry.LastCraftId2);
            bw.Write(entry.CurLastCraftSlot);
            bw.Write(entry.IsGratitudeSpent);
            bw.Write(entry.IsCurrentCraftGratitude);
            bw.Write(entry.WorkerIsPaused);

            if (entry.Queue != null)
            {
                bw.Write((ushort)entry.Queue.Count);
                for (int i = 0; i < entry.Queue.Count; i++)
                {
                    bw.Write(entry.Queue[i].Id);
                    bw.Write(entry.Queue[i].N);
                    bw.Write(entry.Queue[i].Infinite);
                    bw.Write(entry.Queue[i].IsGratitudePointsCraft);
                }
            }
            else
            {
                bw.Write((ushort)0);
            }
        }

        private void OnCraftSyncReceived(CSteamID senderID, byte[] payload)
        {
            if (!IsSyncEnabled) return;
            if (payload == null || payload.Length == 0) return;
            if (!IsOnline) return;

            try
            {
                using (var stream = new MemoryStream(payload))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    byte version = reader.ReadByte();
                    if (version != PayloadVersion)
                    {
                        CoopMod.Logger.LogWarning($"{LogPrefix} Unknown payload version {version}, expected {PayloadVersion}");
                        return;
                    }

                    byte subType = reader.ReadByte();
                    if (!CheckSequence(reader, senderID)) return;

                    if (subType == SubCraftFinished)
                    {
                        HandleCraftFinished(senderID, reader);
                        return;
                    }
                    if (subType != SubSnapshot)
                    {
                        CoopMod.Logger.LogWarning(
                            $"{LogPrefix} Unknown payload subtype {subType}");
                        return;
                    }

                    int entryCount = reader.ReadUInt16();

                    for (int i = 0; i < entryCount; i++)
                    {
                        var entry = ReadEntry(reader);
                        if (entry == null) continue;

                        WorldGameObject wgo = ResolveWGO(entry);
                        if (wgo == null)
                        {
                            CoopMod.Logger.LogDebug($"{LogPrefix} Could not resolve WGO: uid={entry.UniqueId}, obj_id={entry.ObjId}, tag={entry.CustomTag}");
                            continue;
                        }

                        bool applied = ApplyEntry(wgo, entry);
                        if (applied && IsHost)
                        {
                            // Relay the accepted client mutation as host-canonical
                            // state, including an inactive final entry.
                            dirtyWgoUniqueIds.Add(wgo.unique_id);
                        }
                    }

                    ApplyEchoSuppress();
                    if (IsHost)
                    {
                        ForceNextSend = true;
                    }
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Error processing snapshot: {ex.Message}");
            }
        }

        private void HandleCraftFinished(
            CSteamID senderID,
            BinaryReader reader)
        {
            var onlineCoop = OnlineCoopManager.Instance;
            if (!IsHost ||
                onlineCoop == null ||
                !onlineCoop.IsRemotePlayer(senderID) ||
                MainGame.me?.save == null)
            {
                return;
            }

            string craftId = reader.ReadString();
            CraftDefinition craft =
                GameBalance.me?.GetDataOrNull<CraftDefinition>(craftId);
            if (craft == null)
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Ignored unknown completed craft '{craftId}'");
                return;
            }

            isApplyingRemoteCraftCompletion = true;
            try
            {
                // Apply only durable completion hooks. Re-running
                // ProcessFinishedCraft here would duplicate physical outputs.
                MainGame.me.save.OnFinishedCraft(craft);
            }
            finally
            {
                isApplyingRemoteCraftCompletion = false;
            }

            TechSync.Instance?.MarkDirty();
            QuestSync.Instance?.MarkDirty();
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Applied client craft completion to host progression: {craftId}");
        }

        private static CraftStateEntry ReadEntry(BinaryReader reader)
        {
            var entry = new CraftStateEntry();
            entry.UniqueId = reader.ReadInt64();
            entry.ObjId = reader.ReadString();
            entry.CustomTag = reader.ReadString();
            entry.PosX = reader.ReadSingle();
            entry.PosY = reader.ReadSingle();
            entry.PosZ = reader.ReadSingle();
            entry.Available = reader.ReadBoolean();
            entry.IsCrafting = reader.ReadBoolean();
            entry.CurrentCraftId = reader.ReadString();
            entry.CraftAmount = reader.ReadInt32();
            entry.Progress = reader.ReadSingle();
            entry.CurItemId = reader.ReadString();
            entry.CurItemDur = reader.ReadSingle();
            entry.DurItemId = reader.ReadString();
            entry.DurItemDur = reader.ReadSingle();
            entry.MultiqualityItemId = reader.ReadString();
            entry.HasMultiqualityResult = reader.ReadBoolean();
            if (entry.HasMultiqualityResult)
            {
                entry.MultiqualityResultValueItems = reader.ReadSingle();
                entry.MultiqualityResultValuePerks = reader.ReadSingle();
                entry.MultiqualityResultValueDifficulty = reader.ReadSingle();
                entry.MultiqualityResultQp1 = reader.ReadSingle();
                entry.MultiqualityResultQp2 = reader.ReadSingle();
                entry.MultiqualityResultQp3 = reader.ReadSingle();
            }
            entry.LastCraftId = reader.ReadString();
            entry.LastCraftId2 = reader.ReadString();
            entry.CurLastCraftSlot = reader.ReadInt32();
            entry.IsGratitudeSpent = reader.ReadBoolean();
            entry.IsCurrentCraftGratitude = reader.ReadBoolean();
            entry.WorkerIsPaused = reader.ReadBoolean();

            int queueCount = reader.ReadUInt16();
            entry.Queue = new List<CraftQueueEntry>(queueCount);
            for (int i = 0; i < queueCount; i++)
            {
                entry.Queue.Add(new CraftQueueEntry
                {
                    Id = reader.ReadString(),
                    N = reader.ReadInt32(),
                    Infinite = reader.ReadBoolean(),
                    IsGratitudePointsCraft = reader.ReadBoolean()
                });
            }

            return entry;
        }

        private WorldGameObject ResolveWGO(CraftStateEntry entry)
        {
            if (entry.UniqueId > 0)
            {
                var wgo = WorldMap.GetWorldGameObjectByUniqueId(entry.UniqueId, false);
                if (wgo != null) return wgo;
            }

            if (!string.IsNullOrEmpty(entry.CustomTag))
            {
                var wgo = WorldMap.GetWorldGameObjectByCustomTag(entry.CustomTag, true);
                if (wgo != null) return wgo;
            }

            if (!string.IsNullOrEmpty(entry.ObjId) && MainGame.me != null)
            {
                var objects = WGORegistry.Instance?.SnapshotAll() ?? MainGame.me?.GetListOfWorldObjects();
                if (objects != null)
                {
                    WorldGameObject nearest = null;
                    float nearestDist = 96f * 96f;
                    var pos = new Vector3(entry.PosX, entry.PosY, entry.PosZ);

                    for (int i = 0; i < objects.Count; i++)
                    {
                        var candidate = objects[i];
                        if (candidate == null || candidate.obj_id != entry.ObjId) continue;
                        float dist = (candidate.transform.position - pos).sqrMagnitude;
                        if (dist < nearestDist)
                        {
                            nearestDist = dist;
                            nearest = candidate;
                        }
                    }

                    return nearest;
                }
            }

            return null;
        }

        private bool ApplyEntry(WorldGameObject wgo, CraftStateEntry entry)
        {
            var craft = wgo.components?.craft;
            if (craft == null) return false;

            if (ShouldPreserveLocalCraftState(wgo.unique_id))
            {
                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Ignored stale remote craft state for locally-owned " +
                    $"{wgo.obj_id} (uid={wgo.unique_id}): incoming crafting={entry.IsCrafting}, " +
                    $"craft='{entry.CurrentCraftId}', progress={entry.Progress:F2}");
                return false;
            }

            try
            {
                var serializable = new SerializableWGO.SerializableCraft
                {
                    available = entry.Available,
                    is_crafting = entry.IsCrafting,
                    cur_craft_id = entry.CurrentCraftId,
                    cur_item_id = entry.CurItemId,
                    cur_item_dur = entry.CurItemDur,
                    dur_item_id = entry.DurItemId,
                    dur_item_dur = entry.DurItemDur,
                    multiquality_item_id = entry.MultiqualityItemId,
                    multiquality_craft_result = BuildMultiqualityResult(entry),
                    craft_amount = entry.CraftAmount,
                    last_craft_id = entry.LastCraftId,
                    last_craft_id_2 = entry.LastCraftId2,
                    cur_last_craft_slot = entry.CurLastCraftSlot,
                    is_gratitude_points_spent_for_craft = entry.IsGratitudeSpent,
                    queue = BuildQueueList(entry.Queue),
                    cur_craft_items_used = new List<Item>()
                };

                isApplyingRemoteCraftState = true;
                try
                {
                    craft.DeserializeCraftComponent(serializable);
                    wgo.progress = entry.Progress;
                    wgo.is_current_craft_gratitude =
                        entry.IsCurrentCraftGratitude;
                    try { craft.RefreshComponentBubbleData(false); } catch { }
                }
                finally
                {
                    isApplyingRemoteCraftState = false;
                }

                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Applied craft state to {wgo.obj_id} " +
                    $"(uid={wgo.unique_id}): crafting={entry.IsCrafting}, " +
                    $"craft={entry.CurrentCraftId}, progress={entry.Progress:F2}, " +
                    $"queue={entry.Queue?.Count ?? 0}");
                return true;
            }
            catch (Exception ex)
            {
                isApplyingRemoteCraftState = false;
                CoopMod.Logger.LogWarning($"{LogPrefix} Error applying craft state to {wgo.obj_id}: {ex.Message}");
                return false;
            }
        }

        private bool ShouldPreserveLocalCraftState(long uniqueId)
        {
            if (locallyOwnedCrafts.Contains(uniqueId))
                return true;

            if (!localAuthorityReleaseAt.TryGetValue(
                    uniqueId,
                    out float releaseAt))
            {
                return false;
            }

            if (Time.realtimeSinceStartup < releaseAt)
                return true;

            localAuthorityReleaseAt.Remove(uniqueId);
            return false;
        }

        private static List<CraftComponent.CraftQueueItem> BuildQueueList(List<CraftQueueEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                return new List<CraftComponent.CraftQueueItem>();

            var result = new List<CraftComponent.CraftQueueItem>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                result.Add(new CraftComponent.CraftQueueItem
                {
                    id = entries[i].Id,
                    n = entries[i].N,
                    infinite = entries[i].Infinite,
                    is_gratitude_points_craft = entries[i].IsGratitudePointsCraft
                });
            }
            return result;
        }

        private static CraftDefinition.MultiqualityCraftResult BuildMultiqualityResult(CraftStateEntry entry)
        {
            if (!entry.HasMultiqualityResult) return null;
            var result = new CraftDefinition.MultiqualityCraftResult();
            result.value_items = entry.MultiqualityResultValueItems;
            result.value_perks = entry.MultiqualityResultValuePerks;
            result.value_difficulty = entry.MultiqualityResultValueDifficulty;
            result.qp_1 = entry.MultiqualityResultQp1;
            result.qp_2 = entry.MultiqualityResultQp2;
            result.qp_3 = entry.MultiqualityResultQp3;
            return result;
        }

        internal class CraftStateEntry
        {
            public long UniqueId;
            public string ObjId;
            public string CustomTag;
            public float PosX, PosY, PosZ;
            public bool Available;
            public bool IsCrafting;
            public string CurrentCraftId;
            public int CraftAmount;
            public float Progress;
            public string CurItemId;
            public float CurItemDur;
            public string DurItemId;
            public float DurItemDur;
            public string MultiqualityItemId;
            public bool HasMultiqualityResult;
            public float MultiqualityResultValueItems;
            public float MultiqualityResultValuePerks;
            public float MultiqualityResultValueDifficulty;
            public float MultiqualityResultQp1;
            public float MultiqualityResultQp2;
            public float MultiqualityResultQp3;
            public string LastCraftId;
            public string LastCraftId2;
            public int CurLastCraftSlot;
            public bool IsGratitudeSpent;
            public bool IsCurrentCraftGratitude;
            public bool WorkerIsPaused;
            public List<CraftQueueEntry> Queue;

            public string GetKey()
            {
                return $"{UniqueId}:{IsCrafting}:{CurrentCraftId}:{CraftAmount}:{Progress:F3}:{IsGratitudeSpent}:{IsCurrentCraftGratitude}:{Queue?.Count ?? 0}";
            }
        }

        internal class CraftQueueEntry
        {
            public string Id;
            public int N;
            public bool Infinite;
            public bool IsGratitudePointsCraft;
        }
    }
}

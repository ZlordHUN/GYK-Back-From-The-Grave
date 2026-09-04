using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GraveyardKeeperCoop.Network;
using Steamworks;
using UnityEngine;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Mirrors the progress indicator owned by a player who is actively working.
    ///
    /// Graveyard Keeper stores work progress on the target WGO, but renders its
    /// HintPos.AbovePlayer bubble against MainGame.me.player. A remote ghost also
    /// has interaction updates disabled, so vanilla never creates an independently
    /// anchored indicator for it. This component sends explicit ownership/lifecycle
    /// and renders a separate bubble linked to the remote player's bubble transform.
    /// </summary>
    public sealed class WorkIndicatorSync : SyncBehaviour
    {
        public static WorkIndicatorSync Instance => GetInstance<WorkIndicatorSync>();

        private const byte PayloadVersion = 3;
        private const byte LegacyPayloadVersion = 2;
        private const float KeepAliveSeconds = 0.1f;
        private const float RemoteTimeoutSeconds = 3f;

        internal enum IndicatorKind : byte
        {
            None = 0,
            HpProgress = 1,
            CraftingProgress = 2
        }

        private sealed class IndicatorState
        {
            public ulong ActorSteamId;
            public long TargetUniqueId;
            public string TargetObjId = string.Empty;
            public string TargetCustomTag = string.Empty;
            public Vector3 TargetPosition;
            public IndicatorKind Kind;
            public float Progress;
            public string WorkId = string.Empty;
            public bool Resumable;
        }

        private sealed class RemoteIndicatorView
        {
            public IndicatorState State;
            public WorldGameObject Target;
            public InteractionBubbleGUI Bubble;
            public float LastSignalAt;
        }

        private sealed class SharedProgressState
        {
            public IndicatorKind Kind;
            public string WorkId = string.Empty;
            public float Progress;
        }

        private IndicatorState localState;
        private WorldGameObject localTarget;
        private readonly Dictionary<ulong, RemoteIndicatorView> remoteViews =
            new Dictionary<ulong, RemoteIndicatorView>();
        private readonly Dictionary<long, SharedProgressState> sharedProgressByTarget =
            new Dictionary<long, SharedProgressState>();
        private readonly List<ulong> staleRemoteActors = new List<ulong>();
        private float nextKeepAliveAt;

        protected override string LogPrefix => "[WorkIndicatorSync]";

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnWorkIndicatorSyncReceived -= OnWorkIndicatorSyncReceived;
                SteamP2PManager.Instance.OnWorkIndicatorSyncReceived += OnWorkIndicatorSyncReceived;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnWorkIndicatorSyncReceived -= OnWorkIndicatorSyncReceived;
            }
        }

        protected override void OnSyncEnabled()
        {
            localState = null;
            localTarget = null;
            nextKeepAliveAt = 0f;
            sharedProgressByTarget.Clear();
            ClearAllRemoteStates();
        }

        protected override void OnSyncDisabled()
        {
            localState = null;
            localTarget = null;
            sharedProgressByTarget.Clear();
            ClearAllRemoteStates();
        }

        private void Update()
        {
            if (!IsSyncEnabled || !IsOnline)
                return;

            float now = Time.realtimeSinceStartup;
            if (localState != null && !IsLocalStateStillActive())
            {
                ClearLocalState();
            }
            else if (localState != null && now >= nextKeepAliveAt)
            {
                localState.Progress =
                    GetTargetProgress(localTarget, localState.Kind);
                localState.WorkId = GetWorkId(localTarget, localState.Kind);
                localState.Resumable = IsTargetResumable(
                    localTarget,
                    localState.Kind,
                    localState.WorkId);
                RememberSharedProgress(localState, localTarget);
                SendState(localState, true, reliable: false);
                nextKeepAliveAt = now + KeepAliveSeconds;
            }

            if (remoteViews.Count == 0)
                return;

            staleRemoteActors.Clear();
            foreach (var pair in remoteViews)
            {
                if (now - pair.Value.LastSignalAt > RemoteTimeoutSeconds)
                    staleRemoteActors.Add(pair.Key);
                else
                    EnsureRemoteBubble(pair.Value);
            }

            for (int i = 0; i < staleRemoteActors.Count; i++)
            {
                CoopMod.Logger.LogDebug(
                    $"{LogPrefix} Remote indicator timed out for {staleRemoteActors[i]}");
                ClearRemoteState(staleRemoteActors[i]);
            }
        }

        internal static void NotifyLocalToolStarted(ToolComponent tool, WorldGameObject target)
        {
            var instance = Instance;
            if (instance == null || tool?.wgo == null || target == null)
                return;
            if (tool.wgo != MainGame.me?.player)
                return;

            Item equippedTool = tool.wgo.GetEquippedTool();
            if (equippedTool?.definition?.type == ItemDefinition.ItemType.Sword)
                return;

            IndicatorKind kind = GetIndicatorKind(target);
            if (kind == IndicatorKind.None)
                return;

            bool isNewWorkAction = instance.localState == null ||
                instance.localState.TargetUniqueId != target.unique_id ||
                instance.localState.Kind != kind;
            if (kind == IndicatorKind.CraftingProgress && isNewWorkAction)
            {
                CraftSync.Instance?.NotifyLocalCraftWorkStarted(
                    target.components?.craft);
            }

            instance.SetLocalState(target, kind);
        }

        /// <summary>
        /// Identify active workstation work that requires authority before
        /// ToolComponent starts its animation or advances progress. HP work such as
        /// digging remains resumable without an exclusive station lease.
        /// </summary>
        internal static CraftComponent GetExclusiveLocalCraftTarget(
            ToolComponent tool,
            WorldGameObject target)
        {
            if (tool?.wgo == null || target == null || tool.wgo != MainGame.me?.player)
                return null;

            Item equippedTool = tool.wgo.GetEquippedTool();
            if (equippedTool?.definition?.type == ItemDefinition.ItemType.Sword)
                return null;

            if (GetIndicatorKind(target) != IndicatorKind.CraftingProgress)
                return null;

            return target.components?.craft;
        }

        internal static void NotifyLocalToolStopped(ToolComponent tool)
        {
            var instance = Instance;
            if (instance == null || tool?.wgo == null)
                return;
            if (tool.wgo != MainGame.me?.player)
                return;
            if (instance.localState == null)
                return;

            instance.ClearLocalState();
        }

        private static IndicatorKind GetIndicatorKind(WorldGameObject target)
        {
            CraftComponent craft = target.components?.craft;
            if (craft != null &&
                craft.enabled &&
                craft.is_crafting &&
                craft.current_craft != null &&
                !craft.current_craft.is_auto)
            {
                return IndicatorKind.CraftingProgress;
            }

            HPActionComponent hp = target.components?.hp;
            // A completed HP target can remain interactable for a frame while its
            // zero-HP activity replaces it. Reopening the indicator in that window
            // produces an active/inactive packet pair every frame and can flood the
            // reliable queue if the replacement is delayed.
            if (hp != null &&
                hp.enabled &&
                hp.HasHPInDefinition() &&
                target.hp > 0f)
            {
                return IndicatorKind.HpProgress;
            }

            return IndicatorKind.None;
        }

        private void SetLocalState(WorldGameObject target, IndicatorKind kind)
        {
            if (!IsSyncEnabled || !IsOnline || target == null || target.unique_id <= 0)
                return;

            if (localState != null &&
                localState.TargetUniqueId == target.unique_id &&
                localState.Kind == kind)
            {
                return;
            }

            if (localState != null)
                ClearLocalState();

            string workId = GetWorkId(target, kind);
            RestoreSharedProgress(target, kind, workId);
            localState = CaptureState(target, kind);
            localTarget = target;
            RememberSharedProgress(localState, localTarget);
            SendState(localState, true, reliable: true);
            nextKeepAliveAt = Time.realtimeSinceStartup + KeepAliveSeconds;

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Local indicator started: kind={kind}, " +
                $"uid={target.unique_id}, obj={target.obj_id}");
        }

        private void ClearLocalState()
        {
            if (localState == null)
                return;

            localState.Progress = GetTargetProgress(localTarget, localState.Kind);
            localState.WorkId = GetWorkId(localTarget, localState.Kind);
            localState.Resumable = IsTargetResumable(
                localTarget,
                localState.Kind,
                localState.WorkId);
            RememberSharedProgress(localState, localTarget);

            if (localState.Kind == IndicatorKind.CraftingProgress)
            {
                CraftSync.Instance?.NotifyLocalCraftWorkStopped(
                    localTarget?.components?.craft);
            }

            SendState(localState, false, reliable: true);
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Local indicator stopped: kind={localState.Kind}, " +
                $"uid={localState.TargetUniqueId}, obj={localState.TargetObjId}, " +
                $"progress={localState.Progress:F3}, resumable={localState.Resumable}");
            localState = null;
            localTarget = null;
            nextKeepAliveAt = 0f;
            GraveyardKeeperCoop.Patches.AnimationSyncPatch.SendToolStopped();
        }

        private bool IsLocalStateStillActive()
        {
            if (localState == null || localTarget == null)
                return false;

            if (localState.Kind == IndicatorKind.CraftingProgress)
            {
                CraftComponent craft = localTarget.components?.craft;
                return craft != null && craft.is_crafting;
            }

            HPActionComponent hp = localTarget.components?.hp;
            return hp != null && localTarget.hp > 0f;
        }

        private static IndicatorState CaptureState(WorldGameObject target, IndicatorKind kind)
        {
            ulong actorSteamId = SteamManager.Initialized
                ? SteamUser.GetSteamID().m_SteamID
                : 0UL;

            return new IndicatorState
            {
                ActorSteamId = actorSteamId,
                TargetUniqueId = target.unique_id,
                TargetObjId = target.obj_id ?? string.Empty,
                TargetCustomTag = target.custom_tag ?? string.Empty,
                TargetPosition = target.transform != null
                    ? target.transform.position
                    : Vector3.zero,
                Kind = kind,
                Progress = GetTargetProgress(target, kind),
                WorkId = GetWorkId(target, kind),
                Resumable = IsTargetResumable(
                    target,
                    kind,
                    GetWorkId(target, kind))
            };
        }

        private static string GetWorkId(
            WorldGameObject target,
            IndicatorKind kind)
        {
            if (target == null || kind != IndicatorKind.CraftingProgress)
                return string.Empty;

            return target.components?.craft?.current_craft?.id ?? string.Empty;
        }

        private static bool IsTargetResumable(
            WorldGameObject target,
            IndicatorKind kind,
            string workId)
        {
            if (target == null || target.is_removed)
                return false;

            if (kind == IndicatorKind.CraftingProgress)
            {
                CraftComponent craft = target.components?.craft;
                return craft != null &&
                    craft.is_crafting &&
                    craft.current_craft != null &&
                    craft.current_craft.id == workId &&
                    target.progress < 1f;
            }

            HPActionComponent hp = target.components?.hp;
            return hp != null && hp.enabled && target.hp > 0f;
        }

        private static float GetTargetProgress(
            WorldGameObject target,
            IndicatorKind kind)
        {
            if (target == null)
                return 0f;

            if (kind == IndicatorKind.CraftingProgress)
                return Mathf.Clamp01(target.progress);

            HPActionComponent hp = target.components?.hp;
            return hp != null
                ? Mathf.Clamp01(hp.GetHPProgress())
                : 0f;
        }

        private void SendState(
            IndicatorState state,
            bool active,
            bool reliable)
        {
            if (!IsSyncEnabled || !IsOnline || state == null || state.ActorSteamId == 0UL)
                return;

            using (var stream = new MemoryStream(160))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PayloadVersion);
                writer.Write(NextSequence++);
                writer.Write(state.ActorSteamId);
                writer.Write(active);
                writer.Write((byte)state.Kind);
                writer.Write(state.TargetUniqueId);
                writer.Write(state.TargetObjId ?? string.Empty);
                writer.Write(state.TargetCustomTag ?? string.Empty);
                writer.Write(state.TargetPosition.x);
                writer.Write(state.TargetPosition.y);
                writer.Write(state.TargetPosition.z);
                writer.Write(Mathf.Clamp01(state.Progress));
                writer.Write(state.WorkId ?? string.Empty);
                writer.Write(state.Resumable);
                writer.Flush();
                SteamP2PManager.Instance?.BroadcastWorkIndicatorSync(
                    stream.ToArray(),
                    reliable: reliable);
            }
        }

        private void OnWorkIndicatorSyncReceived(CSteamID senderID, byte[] payload)
        {
            if (!IsSyncEnabled ||
                !IsOnline ||
                payload == null ||
                payload.Length == 0 ||
                payload.Length > 4096)
                return;

            try
            {
                using (var stream = new MemoryStream(payload, false))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    byte version = reader.ReadByte();
                    if (version != PayloadVersion &&
                        version != LegacyPayloadVersion)
                    {
                        CoopMod.Logger.LogWarning(
                            $"{LogPrefix} Unknown payload version {version}");
                        return;
                    }

                    if (!CheckSequence(reader, senderID))
                        return;

                    ulong actorSteamId = reader.ReadUInt64();
                    bool active = reader.ReadBoolean();
                    IndicatorKind kind = (IndicatorKind)reader.ReadByte();
                    long targetUniqueId = reader.ReadInt64();
                    string targetObjId = reader.ReadString();
                    string targetCustomTag = reader.ReadString();
                    var targetPosition = new Vector3(
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle());
                    float progress = Mathf.Clamp01(reader.ReadSingle());
                    string workId = version >= PayloadVersion
                        ? reader.ReadString()
                        : string.Empty;
                    bool resumable = version >= PayloadVersion
                        ? reader.ReadBoolean()
                        : active || progress < 0.999f;

                    var online = OnlineCoopManager.Instance;
                    if (actorSteamId != senderID.m_SteamID ||
                        online == null ||
                        !online.IsRemotePlayer(senderID))
                    {
                        return;
                    }

                    if (kind != IndicatorKind.HpProgress &&
                        kind != IndicatorKind.CraftingProgress)
                    {
                        return;
                    }

                    var state = new IndicatorState
                    {
                        ActorSteamId = actorSteamId,
                        TargetUniqueId = targetUniqueId,
                        TargetObjId = targetObjId,
                        TargetCustomTag = targetCustomTag,
                        TargetPosition = targetPosition,
                        Kind = kind,
                        Progress = progress,
                        WorkId = workId,
                        Resumable = resumable
                    };

                    WorldGameObject resolvedTarget = ResolveTarget(state);
                    if (string.IsNullOrEmpty(state.WorkId) &&
                        state.Kind == IndicatorKind.CraftingProgress)
                    {
                        state.WorkId = GetWorkId(resolvedTarget, state.Kind);
                    }
                    if (version == LegacyPayloadVersion && !active)
                    {
                        state.Resumable = IsTargetResumable(
                            resolvedTarget,
                            state.Kind,
                            state.WorkId);
                    }
                    RememberSharedProgress(state, resolvedTarget);
                    if (state.Resumable)
                    {
                        RestoreSharedProgress(
                            resolvedTarget,
                            state.Kind,
                            state.WorkId);
                    }

                    if (!active)
                    {
                        ClearRemoteState(actorSteamId);
                        return;
                    }

                    if (!remoteViews.TryGetValue(
                            actorSteamId,
                            out RemoteIndicatorView view))
                    {
                        view = new RemoteIndicatorView();
                        remoteViews[actorSteamId] = view;
                    }

                    bool changed = view.State == null ||
                        view.State.TargetUniqueId != targetUniqueId ||
                        view.State.Kind != kind ||
                        view.State.WorkId != state.WorkId;

                    if (changed)
                    {
                        DestroyRemoteBubble(view);
                        view.Target = resolvedTarget;
                        CoopMod.Logger.LogDebug(
                            $"{LogPrefix} Remote indicator started: kind={kind}, " +
                            $"uid={targetUniqueId}, obj={targetObjId}");
                    }
                    else if (view.Target == null)
                    {
                        view.Target = resolvedTarget;
                    }

                    view.State = state;
                    view.LastSignalAt = Time.realtimeSinceStartup;
                    EnsureRemoteBubble(view);
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Error processing indicator state: {ex.Message}");
            }
        }

        private void EnsureRemoteBubble(RemoteIndicatorView view)
        {
            if (view?.State == null)
                return;

            // CraftSync applies the real CraftComponent progress on every peer, so the
            // target already owns a vanilla CraftingProgress widget. Creating a second
            // copied bubble here rendered the same autopsy progress twice. HP work does
            // not have an equivalent remote-player presentation and still needs this
            // explicitly anchored bubble.
            if (view.State.Kind == IndicatorKind.CraftingProgress)
            {
                DestroyRemoteBubble(view);
                return;
            }

            if (view.Target == null)
                view.Target = ResolveTarget(view.State);

            PlayerComponent remotePlayer = OnlineCoopManager.Instance
                ?.GetRemotePlayerComponent(new CSteamID(view.State.ActorSteamId));
            Transform anchor = remotePlayer?.wgo?.bubble_pos_tf;
            InteractionBubbleGUI template = GUIElements.me?.interaction_bubble;
            if (anchor == null || template == null)
                return;

            if (view.Bubble == null)
            {
                WorldGameObject bubbleContext =
                    view.Target ?? remotePlayer.wgo;
                if (bubbleContext == null)
                    return;

                view.Bubble = template.Copy(
                    template.transform.parent,
                    true,
                    $"Remote Work Indicator {view.State.ActorSteamId}");

                var data = new BubbleWidgetDataContainer(bubbleContext);
                var progress = new BubbleWidgetProgressData(
                    () => GetRemoteProgress(view),
                    0,
                    2)
                {
                    widget_id = view.State.Kind == IndicatorKind.CraftingProgress
                        ? BubbleWidgetData.WidgetID.CraftingProgress
                        : BubbleWidgetData.WidgetID.HPProgress
                };
                data.AddData(progress);

                view.Bubble.Show(data, true);
                view.Bubble.LinkTransform(anchor);
                view.Bubble.RefreshAlign(bubbleContext);

                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Showing {view.State.Kind} above remote player " +
                    $"for {view.State.TargetObjId} " +
                    $"(uid={view.State.TargetUniqueId}, " +
                    $"target_resolved={view.Target != null})");
            }
            else
            {
                view.Bubble.LinkTransform(anchor);
            }
        }

        private static float GetRemoteProgress(RemoteIndicatorView view)
        {
            if (view?.State == null)
                return -1f;

            return Mathf.Clamp01(view.State.Progress);
        }

        private WorldGameObject ResolveTarget(IndicatorState state)
        {
            if (state == null)
                return null;

            if (state.TargetUniqueId > 0)
            {
                if (WGORegistry.Instance != null &&
                    WGORegistry.Instance.TryGet(state.TargetUniqueId, out WorldGameObject indexed) &&
                    indexed != null)
                {
                    return indexed;
                }

                WorldGameObject byId =
                    WorldMap.GetWorldGameObjectByUniqueId(state.TargetUniqueId, false);
                if (byId != null)
                    return byId;
            }

            if (!string.IsNullOrEmpty(state.TargetCustomTag))
            {
                WorldGameObject byTag =
                    WorldMap.GetWorldGameObjectByCustomTag(state.TargetCustomTag, true);
                if (byTag != null)
                    return byTag;
            }

            if (string.IsNullOrEmpty(state.TargetObjId))
                return null;

            var objects = WGORegistry.Instance?.SnapshotAll() ??
                MainGame.me?.GetListOfWorldObjects();
            if (objects == null)
                return null;

            WorldGameObject nearest = null;
            float nearestDistance = 96f * 96f;
            for (int i = 0; i < objects.Count; i++)
            {
                WorldGameObject candidate = objects[i];
                if (candidate == null ||
                    candidate.obj_id != state.TargetObjId ||
                    candidate.transform == null)
                {
                    continue;
                }

                float distance =
                    (candidate.transform.position - state.TargetPosition).sqrMagnitude;
                if (distance < nearestDistance)
                {
                    nearest = candidate;
                    nearestDistance = distance;
                }
            }

            return nearest;
        }

        private void RememberSharedProgress(
            IndicatorState state,
            WorldGameObject target)
        {
            if (state == null)
                return;

            long targetId = target != null && target.unique_id > 0
                ? target.unique_id
                : state.TargetUniqueId;
            if (targetId <= 0)
                return;

            if (!state.Resumable)
            {
                sharedProgressByTarget.Remove(targetId);
                return;
            }

            if (target != null)
            {
                state.Progress = Mathf.Max(
                    state.Progress,
                    GetTargetProgress(target, state.Kind));
            }

            if (sharedProgressByTarget.TryGetValue(
                    targetId,
                    out SharedProgressState existing) &&
                existing.Kind == state.Kind &&
                existing.WorkId == state.WorkId)
            {
                state.Progress = Mathf.Max(state.Progress, existing.Progress);
                existing.Progress = state.Progress;
                return;
            }

            sharedProgressByTarget[targetId] = new SharedProgressState
            {
                Kind = state.Kind,
                WorkId = state.WorkId ?? string.Empty,
                Progress = Mathf.Clamp01(state.Progress)
            };
        }

        private void RestoreSharedProgress(
            WorldGameObject target,
            IndicatorKind kind,
            string workId)
        {
            if (target == null || target.unique_id <= 0 ||
                !sharedProgressByTarget.TryGetValue(
                    target.unique_id,
                    out SharedProgressState shared) ||
                shared.Kind != kind ||
                shared.WorkId != (workId ?? string.Empty))
            {
                return;
            }

            float currentProgress = GetTargetProgress(target, kind);
            if (shared.Progress <= currentProgress + 0.0001f)
                return;

            if (kind == IndicatorKind.CraftingProgress)
            {
                CraftComponent craft = target.components?.craft;
                if (craft != null &&
                    craft.is_crafting &&
                    craft.current_craft != null &&
                    craft.current_craft.id == shared.WorkId)
                {
                    CombatSync.ApplyRemoteState(() =>
                        target.progress = Mathf.Clamp01(shared.Progress));
                }
                return;
            }

            float maxHp = GetTargetMaxHp(target);
            if (maxHp <= 0f)
                return;

            float remainingHp = maxHp * (1f - shared.Progress);
            if (remainingHp < target.hp)
            {
                CombatSync.ApplyRemoteState(() =>
                    target.hp = Mathf.Max(0f, remainingHp));
            }
        }

        internal float ClampIncomingHp(
            WorldGameObject target,
            float incomingHp)
        {
            if (target == null || target.unique_id <= 0 ||
                !sharedProgressByTarget.TryGetValue(
                    target.unique_id,
                    out SharedProgressState shared) ||
                shared.Kind != IndicatorKind.HpProgress)
            {
                return incomingHp;
            }

            float maxHp = GetTargetMaxHp(target);
            if (maxHp <= 0f)
                return incomingHp;

            float sharedRemainingHp = maxHp * (1f - shared.Progress);
            return Mathf.Min(
                incomingHp,
                target.hp,
                Mathf.Max(0f, sharedRemainingHp));
        }

        internal float ClampIncomingProgress(
            WorldGameObject target,
            float incomingProgress)
        {
            if (target == null || target.unique_id <= 0 ||
                !sharedProgressByTarget.TryGetValue(
                    target.unique_id,
                    out SharedProgressState shared) ||
                shared.Kind != IndicatorKind.CraftingProgress ||
                shared.WorkId != GetWorkId(target, IndicatorKind.CraftingProgress))
            {
                return incomingProgress;
            }

            return Mathf.Max(incomingProgress, target.progress, shared.Progress);
        }

        private static float GetTargetMaxHp(WorldGameObject target)
        {
            try
            {
                return target?.obj_def?.hp?.EvaluateFloat(target, null) ?? 0f;
            }
            catch
            {
                return 0f;
            }
        }

        private void ClearRemoteState(ulong actorSteamId)
        {
            if (!remoteViews.TryGetValue(
                    actorSteamId,
                    out RemoteIndicatorView view))
            {
                return;
            }

            if (view.State != null)
            {
                CoopMod.Logger.LogDebug(
                    $"{LogPrefix} Remote indicator stopped: kind={view.State.Kind}, " +
                    $"uid={view.State.TargetUniqueId}, obj={view.State.TargetObjId}");
            }

            remoteViews.Remove(actorSteamId);
            DestroyRemoteBubble(view);

            OnlineCoopManager.Instance?.ClearRemoteWorkAnimation(
                new CSteamID(actorSteamId));
        }

        private void ClearAllRemoteStates()
        {
            foreach (RemoteIndicatorView view in remoteViews.Values)
                DestroyRemoteBubble(view);
            remoteViews.Clear();
            staleRemoteActors.Clear();
        }

        private void DestroyRemoteBubble(RemoteIndicatorView view)
        {
            if (view == null)
                return;

            view.Target = null;
            if (view.Bubble == null)
                return;

            if (view.Bubble.gameObject != null)
                Destroy(view.Bubble.gameObject);
            view.Bubble = null;
        }
    }
}

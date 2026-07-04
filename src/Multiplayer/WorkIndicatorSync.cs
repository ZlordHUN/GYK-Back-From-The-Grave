using System;
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

        private const byte PayloadVersion = 2;
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
        }

        private IndicatorState localState;
        private IndicatorState remoteState;
        private WorldGameObject localTarget;
        private WorldGameObject remoteTarget;
        private InteractionBubbleGUI remoteBubble;
        private float nextKeepAliveAt;
        private float remoteLastSignalAt;

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
            remoteState = null;
            localTarget = null;
            nextKeepAliveAt = 0f;
            remoteLastSignalAt = 0f;
            DestroyRemoteBubble();
        }

        protected override void OnSyncDisabled()
        {
            localState = null;
            remoteState = null;
            localTarget = null;
            DestroyRemoteBubble();
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
                SendState(localState, true);
                nextKeepAliveAt = now + KeepAliveSeconds;
            }

            if (remoteState == null)
                return;

            if (now - remoteLastSignalAt > RemoteTimeoutSeconds)
            {
                CoopMod.Logger.LogDebug($"{LogPrefix} Remote indicator timed out");
                ClearRemoteState();
                return;
            }

            EnsureRemoteBubble();
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

            instance.SetLocalState(target, kind);
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
            if (hp != null && hp.enabled && hp.HasHPInDefinition())
                return IndicatorKind.HpProgress;

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
                SendState(localState, false);

            localState = CaptureState(target, kind);
            localTarget = target;
            SendState(localState, true);
            nextKeepAliveAt = Time.realtimeSinceStartup + KeepAliveSeconds;

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Local indicator started: kind={kind}, " +
                $"uid={target.unique_id}, obj={target.obj_id}");
        }

        private void ClearLocalState()
        {
            if (localState == null)
                return;

            SendState(localState, false);
            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Local indicator stopped: kind={localState.Kind}, " +
                $"uid={localState.TargetUniqueId}, obj={localState.TargetObjId}");
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
                Progress = GetTargetProgress(target, kind)
            };
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

        private void SendState(IndicatorState state, bool active)
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
                writer.Flush();
                SteamP2PManager.Instance?.BroadcastWorkIndicatorSync(stream.ToArray());
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
                    if (version != PayloadVersion)
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

                    var online = OnlineCoopManager.Instance;
                    if (actorSteamId != senderID.m_SteamID ||
                        online == null ||
                        senderID != online.RemotePlayerSteamID)
                    {
                        return;
                    }

                    if (!active)
                    {
                        if (remoteState == null ||
                            remoteState.ActorSteamId == actorSteamId)
                        {
                            ClearRemoteState();
                        }
                        return;
                    }

                    if (kind != IndicatorKind.HpProgress &&
                        kind != IndicatorKind.CraftingProgress)
                    {
                        return;
                    }

                    bool changed = remoteState == null ||
                        remoteState.TargetUniqueId != targetUniqueId ||
                        remoteState.Kind != kind ||
                        remoteState.ActorSteamId != actorSteamId;

                    remoteState = new IndicatorState
                    {
                        ActorSteamId = actorSteamId,
                        TargetUniqueId = targetUniqueId,
                        TargetObjId = targetObjId,
                        TargetCustomTag = targetCustomTag,
                        TargetPosition = targetPosition,
                        Kind = kind,
                        Progress = progress
                    };
                    remoteLastSignalAt = Time.realtimeSinceStartup;

                    if (changed)
                    {
                        DestroyRemoteBubble();
                        CoopMod.Logger.LogDebug(
                            $"{LogPrefix} Remote indicator started: kind={kind}, " +
                            $"uid={targetUniqueId}, obj={targetObjId}");
                    }

                    EnsureRemoteBubble();
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"{LogPrefix} Error processing indicator state: {ex.Message}");
            }
        }

        private void EnsureRemoteBubble()
        {
            if (remoteState == null)
                return;

            if (remoteTarget == null)
                remoteTarget = ResolveTarget(remoteState);

            PlayerComponent remotePlayer = OnlineCoopManager.Instance?.RemotePlayerComponent;
            Transform anchor = remotePlayer?.wgo?.bubble_pos_tf;
            InteractionBubbleGUI template = GUIElements.me?.interaction_bubble;
            if (anchor == null || template == null)
                return;

            if (remoteBubble == null)
            {
                WorldGameObject bubbleContext =
                    remoteTarget ?? remotePlayer.wgo;
                if (bubbleContext == null)
                    return;

                remoteBubble = template.Copy(
                    template.transform.parent,
                    true,
                    "Remote Work Indicator");

                var data = new BubbleWidgetDataContainer(bubbleContext);
                var progress = new BubbleWidgetProgressData(GetRemoteProgress, 0, 2)
                {
                    widget_id = remoteState.Kind == IndicatorKind.CraftingProgress
                        ? BubbleWidgetData.WidgetID.CraftingProgress
                        : BubbleWidgetData.WidgetID.HPProgress
                };
                data.AddData(progress);

                remoteBubble.Show(data, true);
                remoteBubble.LinkTransform(anchor);
                remoteBubble.RefreshAlign(bubbleContext);

                CoopMod.Logger.LogInfo(
                    $"{LogPrefix} Showing {remoteState.Kind} above remote player " +
                    $"for {remoteState.TargetObjId} " +
                    $"(uid={remoteState.TargetUniqueId}, " +
                    $"target_resolved={remoteTarget != null})");
            }
            else
            {
                remoteBubble.LinkTransform(anchor);
            }
        }

        private float GetRemoteProgress()
        {
            if (remoteState == null)
                return -1f;

            return Mathf.Clamp01(remoteState.Progress);
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

        private void ClearRemoteState()
        {
            bool hadRemoteState = remoteState != null;
            if (remoteState != null)
            {
                CoopMod.Logger.LogDebug(
                    $"{LogPrefix} Remote indicator stopped: kind={remoteState.Kind}, " +
                    $"uid={remoteState.TargetUniqueId}, obj={remoteState.TargetObjId}");
            }

            remoteState = null;
            remoteLastSignalAt = 0f;
            DestroyRemoteBubble();

            if (hadRemoteState)
                OnlineCoopManager.Instance?.ClearRemoteWorkAnimation();
        }

        private void DestroyRemoteBubble()
        {
            remoteTarget = null;
            if (remoteBubble == null)
                return;

            if (remoteBubble.gameObject != null)
                Destroy(remoteBubble.gameObject);
            remoteBubble = null;
        }
    }
}

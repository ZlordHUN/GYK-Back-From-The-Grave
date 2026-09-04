using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using Steamworks;
using HarmonyLib;
using GraveyardKeeperCoop.Network;
using GraveyardKeeperCoop.Utils;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Synchronizes game time (day and time of day) between host and clients.
    /// Host broadcasts time frequently enough to keep the client clock smooth,
    /// with a higher cadence while the synchronized sleep clock runs at 10x.
    /// Clients receive and apply the time to stay in sync.
    /// </summary>
    public class GameTimeSync : SyncBehaviour
    {
        public static GameTimeSync Instance => GetInstance<GameTimeSync>();

        public const string MSG_TIME_SYNC = "TIME:";

        private const float NormalSyncHz = 2f;
        private const float FastForwardSyncHz = 10f;
        private const float ClockSkewDiagnosticMinutes = 2f;
        private const float ClockSkewDiagnosticIntervalSeconds = 5f;
        private const float SleepRosterCheckSeconds = 0.5f;
        private const float SleepSnapshotRepeatSeconds = 2f;
        private const byte SleepMessageState = 0;
        private const byte SleepMessageSnapshot = 1;
        private const byte SleepMessageWakeAll = 2;
        private const byte SleepMessageWakeComplete = 3;
        private const byte SleepRosterTailVersion = 1;
        private const int MaxSleepRosterMembers = 4;
        private const int MaxCatchUpDaysPerPacket = 14;
        private const float WakeCompletionTimeoutSeconds = 15f;
        private const string RemoteSleepSpriteName = "001_sleep";
        private const string HeroBedTag = "hero_bed";
        private const string FirstSleepGhostPendingParam =
            "ghost_comes_2_home_after_1st_burial";
        private const float RemoteSleepLabelClearance = -12f;
        private const float PartyBedMinimumSpacing = 54f;
        private float lastSendTime = 0f;
        private float nextClockSkewDiagnosticAt;
        private float nextSleepRosterCheckAt;
        private float nextSleepSnapshotAt;
        private static MethodInfo onEndOfDayMethod;
        private static MethodInfo sleepWakeUpMethod;
        private static Sprite remoteSleepSprite;
        private static bool remoteSleepSpriteMissingLogged;
        private static bool remoteSleepSpriteSourceLogged;

        private sealed class SleepPeerState
        {
            public bool Sleeping;
            public bool ReadyToWake;
        }

        private sealed class RemoteSleepRendererState
        {
            public ulong SteamId;
            public Transform CharacterRoot;
            public WorldGameObject MarkerTarget;
            public GameObject WorldSleepPose;
            public SpriteRenderer WorldSleepRenderer;
            public Transform WorldSleepLabelAnchor;
            public int WorldSleepPoseCreatedFrame = -1;
            public bool WorldSleepPoseLogged;
            public SleepGUI PartySleepGui;
            public UI2DSprite PartyBedSprite;
            public UI2DSprite PartySleeperSprite;
            public readonly Dictionary<int, RemoteSleepRendererEntry> Renderers =
                new Dictionary<int, RemoteSleepRendererEntry>();
        }

        private sealed class RemoteSleepRendererEntry
        {
            public SpriteRenderer Renderer;
            public bool WasEnabled;
        }

        private readonly Dictionary<ulong, SleepPeerState> hostSleepStates =
            new Dictionary<ulong, SleepPeerState>();
        private readonly HashSet<ulong> sleepingPeerRoster =
            new HashSet<ulong>();
        private readonly HashSet<ulong> pendingWakeCompletionPeers =
            new HashSet<ulong>();
        private readonly Dictionary<ulong, RemoteSleepRendererState> remoteSleepRenderers =
            new Dictionary<ulong, RemoteSleepRendererState>();
        private bool localSleeping;
        private bool localReadyToWake;
        private bool fastForwardAuthorized;
        private bool systemWakeInProgress;
        private bool sleepClockOwned;
        private bool suppressNextSynchronizedWakeSpeech;
        private bool suppressNextSynchronizedFirstSleepStory;
        private bool wakeCompletionBarrierActive;
        private bool awaitingLocalWakeCompletion;
        private bool localWakeTransitionComplete;
        private uint wakeCompletionCycle;
        private float wakeCompletionBarrierStartedAt;
        private int wakeCompletionExpectedPeerCount;
        private GJCommons.VoidDelegate deferredHostWakeContinuation;
        private uint sleepCycle;
        private int lastBroadcastSleeping = -1;
        private int lastBroadcastTotal = -1;
        private bool lastBroadcastFastForward;
        private int lastAnnouncedSleeping = -1;
        private int lastAnnouncedTotal = -1;
        private bool unknownSleepRosterTailLogged;
        private bool invalidSleepRosterTailLogged;
        private bool firstSleepManualWakeBlockedMessageShown;

        protected override string LogPrefix => "[GameTimeSync]";

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnTimeSyncReceived -= OnTimeSyncReceived;
                SteamP2PManager.Instance.OnTimeSyncReceived += OnTimeSyncReceived;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnTimeSyncReceived -= OnTimeSyncReceived;
            }
        }

        protected override void OnSyncEnabled()
        {
            lastSendTime = 0f;
            nextClockSkewDiagnosticAt = 0f;
            ResetSleepCoordinator(wakeLocal: false);
        }

        protected override void OnSyncDisabled()
        {
            ResetSleepCoordinator(wakeLocal: true);
        }

        private void Update()
        {
            if (!IsSyncEnabled) return;

            if (!IsOnline) return;

            TickWakeCompletionBarrier();
            ApplySleepClockPolicy();

            if (!IsHost)
            {
                DisableClientAutoTime();
                return;
            }

            if (Time.realtimeSinceStartup >= nextSleepRosterCheckAt)
            {
                nextSleepRosterCheckAt =
                    Time.realtimeSinceStartup + SleepRosterCheckSeconds;
                ReconcileHostSleepRoster();
            }

            float syncHz = fastForwardAuthorized
                ? FastForwardSyncHz
                : NormalSyncHz;
            if (Time.realtimeSinceStartup - lastSendTime < (1f / syncHz)) return;

            lastSendTime = Time.realtimeSinceStartup;
            SendTimeSync();
        }

        private void LateUpdate()
        {
            if (!IsSyncEnabled || !IsOnline || sleepingPeerRoster.Count == 0)
                return;

            ReapplyAllRemoteSleepPresentations();
        }

        public void SendTimeSync()
        {
            if (MainGame.me == null || MainGame.me.save == null || TimeOfDay.me == null) return;

            int day = MainGame.me.save.day;
            float timeK = TimeOfDay.me.GetTimeK();

            SteamP2PManager.Instance?.BroadcastTimeSync(day, timeK);
        }

        public bool IsLocalSleeping => localSleeping;
        public bool IsLocalSleepWaitingForParty =>
            IsSyncEnabled && IsOnline && localSleeping && !fastForwardAuthorized;
        public bool IsSystemWakeInProgress => systemWakeInProgress;

        public bool IsPeerSleeping(CSteamID peer)
        {
            if (!IsSyncEnabled || !IsOnline || peer == CSteamID.Nil)
                return false;

            if (peer == SteamUser.GetSteamID())
                return localSleeping;

            return sleepingPeerRoster.Contains(peer.m_SteamID);
        }

        public void ReapplyRemoteSleepPresentation(CSteamID peer)
        {
            if (!IsPeerSleeping(peer))
                return;

            ApplyRemoteSleepPresentation(peer.m_SteamID);
        }

        /// <summary>Called after the local vanilla SleepGUI successfully opens.</summary>
        public void NotifyLocalSleepStarted()
        {
            if (!IsSyncEnabled || !IsOnline || localSleeping)
                return;

            suppressNextSynchronizedWakeSpeech = false;
            suppressNextSynchronizedFirstSleepStory = false;
            firstSleepManualWakeBlockedMessageShown = false;
            localSleeping = true;
            localReadyToWake = false;
            fastForwardAuthorized = false;
            ApplySleepClockPolicy();

            CoopMod.Logger.LogInfo("[SleepSync] Local player entered bed");
            SubmitLocalSleepState();
        }

        /// <summary>
        /// Called before vanilla SleepGUI.WakeUp. Automatic wake is held until every
        /// sleeping player is restored. A lone sleeper can still leave and initiate
        /// the normal first-sleep story scene. Once multiple players are in bed for
        /// that scene, their wake must stay coordinated so only one copy starts.
        /// </summary>
        public bool HandleLocalWakeAttempt(bool manualWake)
        {
            if (!IsSyncEnabled || !IsOnline || !localSleeping || systemWakeInProgress)
                return true;

            if (manualWake)
            {
                if (IsFirstSleepStoryWakeGateActive() &&
                    sleepingPeerRoster.Count > 1)
                {
                    if (!firstSleepManualWakeBlockedMessageShown)
                    {
                        firstSleepManualWakeBlockedMessageShown = true;
                        CoopMod.Logger.LogInfo(
                            "[SleepSync] Held manual wake because multiple " +
                            "players are sleeping for the first-sleep story event");
                        ChatManager.AddMessage(
                            "[System] Sleeping players must wake together for this story event");
                    }
                    return false;
                }

                CoopMod.Logger.LogInfo("[SleepSync] Local player left bed manually");
                suppressNextSynchronizedWakeSpeech = false;
                suppressNextSynchronizedFirstSleepStory = false;
                firstSleepManualWakeBlockedMessageShown = false;
                localSleeping = false;
                localReadyToWake = false;
                fastForwardAuthorized = false;
                RestoreNormalSleepClock();
                SubmitLocalSleepState();
                return true;
            }

            if (!localReadyToWake)
            {
                localReadyToWake = true;
                CoopMod.Logger.LogInfo("[SleepSync] Local player is fully rested and waiting for the party");
                SubmitLocalSleepState();
            }

            return false;
        }

        private static bool IsFirstSleepStoryWakeGateActive()
        {
            WorldGameObject player = MainGame.me?.player;
            return player != null &&
                   player.GetParam(FirstSleepGhostPendingParam, 0f) >= 0.5f;
        }

        internal bool ShouldSuppressLocalFirstSleepWakeSetup =>
            suppressNextSynchronizedFirstSleepStory &&
            IsFirstSleepStoryWakeGateActive();

        internal bool TryConsumeSynchronizedFirstSleepStorySuppression(
            string scriptName)
        {
            if (!suppressNextSynchronizedFirstSleepStory ||
                !string.Equals(
                    scriptName,
                    "ghost_comes_after_players_sleep",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            suppressNextSynchronizedFirstSleepStory = false;
            return true;
        }

        /// <summary>
        /// Vanilla continues each local sleep FlowScript with a wake-up speech node.
        /// During a synchronized party wake that would make every machine announce the
        /// same line. The host is the deterministic speaker; a client consumes this
        /// one-shot flag while still allowing the node's completion callback to run.
        /// </summary>
        internal bool TryConsumeSynchronizedWakeSpeechSuppression(
            WorldGameObject speaker,
            string text)
        {
            if (!suppressNextSynchronizedWakeSpeech ||
                speaker == null ||
                speaker != MainGame.me?.player ||
                !string.Equals(
                    text,
                    "wake_up_home",
                    StringComparison.Ordinal))
            {
                return false;
            }

            suppressNextSynchronizedWakeSpeech = false;
            return true;
        }

        /// <summary>
        /// SleepGUI invokes its wake callback only after autosave, fade-out, Hide,
        /// and the wake-up quest check complete. Clients may continue their local
        /// sleep graph immediately after acknowledging that boundary. The host
        /// holds its continuation until every member from this sleep cycle has
        /// crossed the same boundary, preventing the post-wake sleep graph and
        /// Yorick cutscene from opening over a slower peer's SleepGUI.
        /// </summary>
        internal void CompleteLocalWakeTransition(
            GJCommons.VoidDelegate continuation)
        {
            if (!IsSyncEnabled || !IsOnline ||
                !awaitingLocalWakeCompletion)
            {
                InvokeWakeContinuation(continuation);
                return;
            }

            uint completedCycle = wakeCompletionCycle;
            awaitingLocalWakeCompletion = false;
            if (!IsHost)
            {
                // Start the client's post-wake graph before acknowledging. Its
                // duplicate wake speech/cutscene are suppressed, and the host's
                // visible speech leaves ample time for that one-frame callback.
                InvokeWakeContinuation(continuation);
                SendWakeCompletion(completedCycle);
                return;
            }

            if (!wakeCompletionBarrierActive ||
                completedCycle != sleepCycle)
            {
                InvokeWakeContinuation(continuation);
                return;
            }

            localWakeTransitionComplete = true;
            // Only start the fail-open deadline once the host has crossed the
            // safe vanilla boundary. Before this callback, SleepGUI still owns
            // its autosave/after-save/fade sequence, including Yorick setup.
            wakeCompletionBarrierStartedAt = Time.realtimeSinceStartup;
            deferredHostWakeContinuation = continuation;
            CoopMod.Logger.LogInfo(
                $"[SleepSync] Host completed wake transition for cycle " +
                $"{completedCycle}; waiting for " +
                $"{pendingWakeCompletionPeers.Count} peer(s)");
            TryReleaseWakeCompletionBarrier("all wake transitions completed");
        }

        public void HandleSleepSyncMessage(CSteamID senderID, ref MsgReader reader)
        {
            if (!IsSyncEnabled || !IsOnline || reader.Remaining < 1)
                return;

            try
            {
                byte messageType = reader.ReadByte();
                if (messageType == SleepMessageState)
                {
                    if (!IsHost || reader.Remaining < 2 ||
                        OnlineCoopManager.Instance?.IsRemotePlayer(senderID) != true)
                    {
                        return;
                    }

                    bool sleeping = reader.ReadBool();
                    bool readyToWake = reader.ReadBool();
                    SetHostSleepState(senderID, sleeping, readyToWake);
                    EvaluateHostSleepState(forceBroadcast: true);
                    return;
                }

                if (messageType == SleepMessageWakeComplete)
                {
                    if (!IsHost || reader.Remaining < 4 ||
                        OnlineCoopManager.Instance?.IsRemotePlayer(senderID) != true)
                    {
                        return;
                    }

                    HandleRemoteWakeCompletion(
                        senderID,
                        reader.ReadUInt32());
                    return;
                }

                if (IsHost || !IsExpectedHost(senderID))
                    return;

                if (messageType == SleepMessageSnapshot)
                {
                    if (reader.Remaining < 13)
                        return;

                    int sleeping = reader.ReadInt32();
                    int total = reader.ReadInt32();
                    bool fastForward = reader.ReadBool();
                    uint cycle = reader.ReadUInt32();
                    HashSet<ulong> sleepingPeers =
                        TryReadSleepRosterTail(
                            ref reader,
                            sleeping,
                            total);
                    ApplySleepSnapshot(
                        sleeping,
                        total,
                        fastForward,
                        cycle,
                        sleepingPeers);
                }
                else if (messageType == SleepMessageWakeAll)
                {
                    if (reader.Remaining < 4)
                        return;

                    sleepCycle = reader.ReadUInt32();
                    PrepareLocalWakeCompletion(sleepCycle);
                    // Newer hosts append the exact clock captured for the wake.
                    // Keeping it as an optional tail preserves compatibility with
                    // older four-byte WakeAll payloads while ensuring this client's
                    // SleepGUI autosave records the host's authoritative timestamp.
                    if (reader.Remaining >= 8)
                    {
                        int finalDay = reader.ReadInt32();
                        float finalTimeK = reader.ReadFloat();
                        if (reader.Remaining >= 4)
                        {
                            uint finalClockSequence = reader.ReadUInt32();
                            SteamP2PManager.Instance?
                                .RecordAppliedTimeSyncSequence(
                                    senderID,
                                    finalClockSequence);
                        }
                        ApplyAuthoritativeTime(finalDay, finalTimeK);
                    }
                    ApplySynchronizedWake();
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[SleepSync] Failed to process sleep message: {ex.Message}");
            }
        }

        private HashSet<ulong> TryReadSleepRosterTail(
            ref MsgReader reader,
            int sleeping,
            int total)
        {
            // Sleep snapshots predate the identity roster. No trailing bytes is
            // therefore a valid snapshot from an older host.
            if (reader.Remaining == 0)
                return null;

            if (reader.Remaining < 2)
            {
                LogInvalidSleepRosterTail("missing version or count");
                return null;
            }

            byte version = reader.ReadByte();
            if (version != SleepRosterTailVersion)
            {
                if (!unknownSleepRosterTailLogged)
                {
                    unknownSleepRosterTailLogged = true;
                    CoopMod.Logger.LogWarning(
                        $"[SleepSync] Ignored unknown sleep-roster tail version {version}");
                }
                return null;
            }

            int count = reader.ReadByte();
            int rosterBytes = count * sizeof(ulong);
            if (count > MaxSleepRosterMembers || count != sleeping ||
                count > total || reader.Remaining < rosterBytes)
            {
                LogInvalidSleepRosterTail(
                    $"count={count}, sleeping={sleeping}, total={total}, " +
                    $"remaining={reader.Remaining}");
                return null;
            }

            if (!TryGetLobbyMembers(out HashSet<ulong> lobbyMembers))
                return null;

            var roster = new HashSet<ulong>();
            for (int i = 0; i < count; i++)
            {
                ulong steamId = reader.ReadUInt64();
                if (steamId == 0 || !lobbyMembers.Contains(steamId) ||
                    !roster.Add(steamId))
                {
                    LogInvalidSleepRosterTail(
                        $"invalid or duplicate member {steamId}");
                    return null;
                }
            }

            return roster;
        }

        private void LogInvalidSleepRosterTail(string details)
        {
            if (invalidSleepRosterTailLogged)
                return;

            invalidSleepRosterTailLogged = true;
            CoopMod.Logger.LogWarning(
                $"[SleepSync] Ignored malformed sleep-roster tail ({details})");
        }

        public void NotifySleepPeerLeft(CSteamID peer)
        {
            if (!IsSyncEnabled || peer == CSteamID.Nil)
                return;

            RemoveSleepingPeer(peer.m_SteamID);

            if (!IsHost)
                return;

            if (hostSleepStates.Remove(peer.m_SteamID))
                EvaluateHostSleepState(forceBroadcast: true);
            if (wakeCompletionBarrierActive &&
                pendingWakeCompletionPeers.Remove(peer.m_SteamID))
            {
                CoopMod.Logger.LogInfo(
                    $"[SleepSync] Removed disconnected peer {peer} from " +
                    $"wake-complete cycle {wakeCompletionCycle}");
                TryReleaseWakeCompletionBarrier(
                    "remaining wake-transition peers disconnected");
            }
        }

        private void SubmitLocalSleepState()
        {
            if (IsHost)
            {
                SetHostSleepState(
                    SteamUser.GetSteamID(),
                    localSleeping,
                    localReadyToWake);
                EvaluateHostSleepState(forceBroadcast: true);
                return;
            }

            using (var writer = new MsgWriter(Op.SleepSync, 4))
            {
                writer.Write(SleepMessageState);
                writer.Write(localSleeping);
                writer.Write(localReadyToWake);
                SteamP2PManager.Instance?.SendToHost(writer.ToArray());
            }
        }

        private void SetHostSleepState(
            CSteamID player,
            bool sleeping,
            bool readyToWake)
        {
            if (player == CSteamID.Nil)
                return;

            if (!sleeping)
            {
                hostSleepStates.Remove(player.m_SteamID);
                return;
            }

            if (!hostSleepStates.TryGetValue(
                    player.m_SteamID,
                    out SleepPeerState state))
            {
                state = new SleepPeerState();
                hostSleepStates[player.m_SteamID] = state;
            }

            state.Sleeping = true;
            state.ReadyToWake = readyToWake;
        }

        private void ReconcileHostSleepRoster()
        {
            if (!TryGetLobbyMembers(out HashSet<ulong> members))
                return;

            bool changed = false;
            var tracked = new List<ulong>(hostSleepStates.Keys);
            for (int i = 0; i < tracked.Count; i++)
            {
                if (members.Contains(tracked[i]))
                    continue;

                hostSleepStates.Remove(tracked[i]);
                changed = true;
            }

            if (lastBroadcastTotal >= 0 && lastBroadcastTotal != members.Count)
                changed = true;

            if (changed)
                EvaluateHostSleepState(forceBroadcast: true);
            else if (hostSleepStates.Count > 0 &&
                     Time.realtimeSinceStartup >= nextSleepSnapshotAt)
                EvaluateHostSleepState(forceBroadcast: true);
        }

        private void EvaluateHostSleepState(bool forceBroadcast)
        {
            if (!IsHost || !TryGetLobbyMembers(out HashSet<ulong> members))
                return;

            int sleepingCount = 0;
            var sleepingPeers = new HashSet<ulong>();
            bool allReadyToWake = members.Count > 0;
            foreach (ulong member in members)
            {
                if (!hostSleepStates.TryGetValue(member, out SleepPeerState state) ||
                    !state.Sleeping)
                {
                    allReadyToWake = false;
                    continue;
                }

                sleepingCount++;
                sleepingPeers.Add(member);
                if (!state.ReadyToWake)
                    allReadyToWake = false;
            }

            bool allSleeping = members.Count > 0 && sleepingCount == members.Count;
            if (allSleeping && !fastForwardAuthorized)
                sleepCycle++;
            fastForwardAuthorized = allSleeping;

            BroadcastSleepSnapshot(
                sleepingCount,
                members.Count,
                fastForwardAuthorized,
                forceBroadcast,
                sleepingPeers);

            if (allSleeping && allReadyToWake)
                HostWakeEveryone(members);
        }

        private void BroadcastSleepSnapshot(
            int sleeping,
            int total,
            bool fastForward,
            bool force,
            HashSet<ulong> sleepingPeers)
        {
            bool changed = sleeping != lastBroadcastSleeping ||
                           total != lastBroadcastTotal ||
                           fastForward != lastBroadcastFastForward;
            if (!force && !changed)
                return;

            lastBroadcastSleeping = sleeping;
            lastBroadcastTotal = total;
            lastBroadcastFastForward = fastForward;
            nextSleepSnapshotAt =
                Time.realtimeSinceStartup + SleepSnapshotRepeatSeconds;
            if (changed)
            {
                CoopMod.Logger.LogInfo(
                    $"[SleepSync] Party sleep state: {sleeping}/{total}, " +
                    $"fast-forward={fastForward}");
            }
            ApplySleepSnapshot(
                sleeping,
                total,
                fastForward,
                sleepCycle,
                sleepingPeers);

            int rosterCount = sleepingPeers?.Count ?? 0;
            using (var writer = new MsgWriter(
                       Op.SleepSync,
                       22 + rosterCount * sizeof(ulong)))
            {
                writer.Write(SleepMessageSnapshot);
                writer.Write(sleeping);
                writer.Write(total);
                writer.Write(fastForward);
                writer.Write(sleepCycle);
                writer.Write(SleepRosterTailVersion);
                writer.Write((byte)rosterCount);
                if (rosterCount > 0)
                {
                    var orderedPeers = new List<ulong>(sleepingPeers);
                    orderedPeers.Sort();
                    for (int i = 0; i < orderedPeers.Count; i++)
                        writer.Write(orderedPeers[i]);
                }
                SteamP2PManager.Instance?.BroadcastBinary(writer.ToArray());
            }
        }

        private void ApplySleepSnapshot(
            int sleeping,
            int total,
            bool fastForward,
            uint cycle,
            HashSet<ulong> sleepingPeers)
        {
            if (sleeping < 0 || total <= 0 || sleeping > total)
                return;

            sleepCycle = cycle;
            fastForwardAuthorized = fastForward && localSleeping;
            ApplySleepClockPolicy();

            // A null roster means this snapshot came from an older compatible
            // build (or had an unknown optional tail). Preserve the last known
            // identity state while still applying its aggregate sleep policy.
            if (sleepingPeers != null)
                ApplySleepingPeerRoster(sleepingPeers);

            if (sleeping != lastAnnouncedSleeping || total != lastAnnouncedTotal)
            {
                lastAnnouncedSleeping = sleeping;
                lastAnnouncedTotal = total;
                ChatManager.AddMessage($"[System] {sleeping}/{total} players asleep");
            }
        }

        private void ApplySleepingPeerRoster(HashSet<ulong> sleepingPeers)
        {
            if (sleepingPeers == null)
                return;

            var normalized = new HashSet<ulong>();
            foreach (ulong steamId in sleepingPeers)
            {
                if (steamId != 0)
                    normalized.Add(steamId);
            }

            bool changed = !sleepingPeerRoster.SetEquals(normalized);
            var presented = new List<ulong>(remoteSleepRenderers.Keys);
            for (int i = 0; i < presented.Count; i++)
            {
                if (!normalized.Contains(presented[i]))
                    RestoreRemoteSleepPresentation(presented[i]);
            }

            sleepingPeerRoster.Clear();
            foreach (ulong steamId in normalized)
                sleepingPeerRoster.Add(steamId);

            ulong localSteamId = SteamUser.GetSteamID().m_SteamID;
            foreach (ulong steamId in sleepingPeerRoster)
            {
                if (steamId != localSteamId)
                    ApplyRemoteSleepPresentation(steamId);
            }

            if (!changed)
                return;

            PlayerTradeManager.Instance?.ClearInteractionPrompt();
            CoopMod.Logger.LogInfo(
                $"[SleepSync] Authoritative sleeping-player roster updated: " +
                $"{sleepingPeerRoster.Count}");
        }

        private void RemoveSleepingPeer(ulong steamId)
        {
            bool changed = sleepingPeerRoster.Remove(steamId);
            RestoreRemoteSleepPresentation(steamId);
            if (changed)
                PlayerTradeManager.Instance?.ClearInteractionPrompt();
        }

        private void ReapplyAllRemoteSleepPresentations()
        {
            ulong localSteamId = SteamUser.GetSteamID().m_SteamID;
            foreach (ulong steamId in sleepingPeerRoster)
            {
                if (steamId == localSteamId)
                    continue;

                if (!remoteSleepRenderers.TryGetValue(
                        steamId,
                        out RemoteSleepRendererState state) ||
                    state?.CharacterRoot == null ||
                    state.MarkerTarget == null)
                {
                    ApplyRemoteSleepPresentation(steamId);
                    continue;
                }

                bool worldPoseVisible = ApplyRemoteSleepWorldPose(state);
                ApplyRemoteSleepNameTag(
                    state,
                    state.MarkerTarget,
                    worldPoseVisible);
                ApplyRemoteSleepUiPresentation(state);
                foreach (RemoteSleepRendererEntry entry in state.Renderers.Values)
                {
                    if (entry?.Renderer != null)
                    {
                        entry.Renderer.enabled =
                            !worldPoseVisible && entry.WasEnabled;
                    }
                }
            }
        }

        private void ApplyRemoteSleepPresentation(ulong steamId)
        {
            PlayerComponent remote = OnlineCoopManager.Instance
                ?.GetRemotePlayerComponent(new CSteamID(steamId));
            WorldGameObject remoteWgo = remote?.wgo;
            if (remote?.transform == null || remoteWgo == null)
            {
                RestoreRemoteSleepPresentation(steamId);
                return;
            }

            Transform characterRoot =
                VisualSyncHelpers.FindChildByName(remote.transform, "character");
            if (remoteSleepRenderers.TryGetValue(
                    steamId,
                    out RemoteSleepRendererState existing) &&
                (existing?.CharacterRoot != characterRoot ||
                 existing.MarkerTarget != remoteWgo))
            {
                RestoreRemoteSleepPresentation(steamId);
            }

            if (!remoteSleepRenderers.TryGetValue(
                    steamId,
                    out RemoteSleepRendererState state))
            {
                state = new RemoteSleepRendererState
                {
                    SteamId = steamId,
                    CharacterRoot = characterRoot,
                    MarkerTarget = remoteWgo
                };
                remoteSleepRenderers[steamId] = state;
                CoopMod.Logger.LogInfo(
                    $"[SleepSync] Showing sleep presentation for remote {steamId}");
            }

            bool worldPoseVisible = ApplyRemoteSleepWorldPose(state);
            ApplyRemoteSleepNameTag(state, remoteWgo, worldPoseVisible);
            ApplyRemoteSleepUiPresentation(state);
            if (characterRoot == null)
                return;

            var staleRendererIds = new List<int>();
            foreach (KeyValuePair<int, RemoteSleepRendererEntry> pair in state.Renderers)
            {
                if (pair.Value?.Renderer == null)
                    staleRendererIds.Add(pair.Key);
            }
            for (int i = 0; i < staleRendererIds.Count; i++)
                state.Renderers.Remove(staleRendererIds[i]);

            SpriteRenderer[] renderers =
                characterRoot.GetComponentsInChildren<SpriteRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                SpriteRenderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                int rendererId = renderer.GetInstanceID();
                if (!state.Renderers.TryGetValue(
                        rendererId,
                        out RemoteSleepRendererEntry entry))
                {
                    entry = new RemoteSleepRendererEntry
                    {
                        Renderer = renderer,
                        WasEnabled = renderer.enabled
                    };
                    state.Renderers[rendererId] = entry;
                }

                renderer.enabled = !worldPoseVisible && entry.WasEnabled;
            }
        }

        private static bool ApplyRemoteSleepWorldPose(
            RemoteSleepRendererState state)
        {
            if (state == null)
                return false;

            WorldGameObject bed = null;
            try
            {
                bed = WorldMap.GetWorldGameObjectByCustomTag(
                    HeroBedTag,
                    false);
            }
            catch { }

            SpriteRenderer bedRenderer = FindHeroBedRenderer(bed);
            Sprite sleepSprite = GetRemoteSleepSprite();
            if (bedRenderer == null || sleepSprite == null)
            {
                DestroyRemoteSleepWorldPose(state);
                return false;
            }

            if (state.WorldSleepRenderer == null)
            {
                var pose = new GameObject(
                    $"Remote Sleep Pose {state.SteamId}");
                state.WorldSleepPose = pose;
                state.WorldSleepRenderer = pose.AddComponent<SpriteRenderer>();
                state.WorldSleepPoseCreatedFrame = Time.frameCount;

                var labelAnchor = new GameObject(
                    $"Remote Sleep Label Anchor {state.SteamId}");
                labelAnchor.transform.SetParent(pose.transform, false);
                state.WorldSleepLabelAnchor = labelAnchor.transform;
            }

            SpriteRenderer renderer = state.WorldSleepRenderer;
            if (state.WorldSleepPose != null &&
                !state.WorldSleepPose.activeSelf)
            {
                state.WorldSleepPose.SetActive(true);
            }
            renderer.gameObject.layer = bedRenderer.gameObject.layer;
            renderer.sprite = sleepSprite;
            renderer.sortingLayerID = bedRenderer.sortingLayerID;
            int topBedSortingOrder = bedRenderer.sortingOrder;
            SpriteRenderer[] bedRenderers =
                bed.GetComponentsInChildren<SpriteRenderer>(true);
            for (int i = 0; i < bedRenderers.Length; i++)
            {
                SpriteRenderer child = bedRenderers[i];
                if (child == null || !child.enabled ||
                    !child.gameObject.activeInHierarchy ||
                    child.sortingLayerID != bedRenderer.sortingLayerID)
                {
                    continue;
                }

                topBedSortingOrder = Mathf.Max(
                    topBedSortingOrder,
                    child.sortingOrder);
            }
            renderer.sortingOrder = topBedSortingOrder + 1;
            renderer.color = Color.white;
            renderer.enabled = true;

            // World sprites get their pixel-sized scale from the WGO visual
            // hierarchy. This pose is an independent root, so it must inherit
            // the bed renderer's world scale explicitly; scale 1 makes the
            // stock 48-PPU sleep sprite effectively sub-pixel in this game.
            float bedPixelsPerUnit = Mathf.Max(
                0.01f,
                bedRenderer.sprite.pixelsPerUnit);
            float spriteScaleRatio =
                sleepSprite.pixelsPerUnit / bedPixelsPerUnit;
            Vector3 bedWorldScale = bedRenderer.transform.lossyScale;
            renderer.transform.rotation = bedRenderer.transform.rotation;
            renderer.transform.localScale = new Vector3(
                Mathf.Abs(bedWorldScale.x) * spriteScaleRatio,
                Mathf.Abs(bedWorldScale.y) * spriteScaleRatio,
                1f);

            // The stock sprite keeps a large transparent 96x96 source rect around
            // its 29x27 visible keeper. Match the vanilla SleepGUI registration,
            // rather than centering that transparent rect over the pillow.
            Bounds bedBounds = bedRenderer.bounds;
            Vector3 desiredCenter = bedBounds.center +
                                    Vector3.up * (bedBounds.extents.y * 0.15f);
            renderer.transform.position = bedRenderer.transform.position;
            renderer.transform.position +=
                desiredCenter - renderer.bounds.center;

            if (state.WorldSleepLabelAnchor == null &&
                state.WorldSleepPose != null)
            {
                var labelAnchor = new GameObject(
                    $"Remote Sleep Label Anchor {state.SteamId}");
                labelAnchor.transform.SetParent(
                    state.WorldSleepPose.transform,
                    false);
                state.WorldSleepLabelAnchor = labelAnchor.transform;
            }
            if (state.WorldSleepLabelAnchor != null)
            {
                Vector3 labelPosition = new Vector3(
                    bedBounds.center.x,
                    bedBounds.max.y + RemoteSleepLabelClearance,
                    renderer.transform.position.z);

                // Keep the label at the same projected screen point as the
                // bed, but at the remote player's normal camera depth. Bed
                // renderers use deep sorting Z values that are unsuitable for
                // world-to-screen UI anchors.
                Transform playerAnchor = state.MarkerTarget?.bubble_pos_tf;
                Camera worldCamera = MainGame.me?.world_cam ?? Camera.main;
                if (worldCamera != null && playerAnchor != null)
                {
                    Vector3 labelScreen =
                        worldCamera.WorldToScreenPoint(labelPosition);
                    labelScreen.z = worldCamera.WorldToScreenPoint(
                        playerAnchor.position).z;
                    labelPosition =
                        worldCamera.ScreenToWorldPoint(labelScreen);
                }
                else if (playerAnchor != null)
                {
                    labelPosition.z = playerAnchor.position.z;
                }

                state.WorldSleepLabelAnchor.position = labelPosition;
            }

            if (state.WorldSleepPose != null)
            {
                bool visible = bedRenderer.enabled &&
                               bedRenderer.gameObject.activeInHierarchy;
                if (state.WorldSleepPose.activeSelf != visible)
                    state.WorldSleepPose.SetActive(visible);

                if (!state.WorldSleepPoseLogged &&
                    Time.frameCount > state.WorldSleepPoseCreatedFrame)
                {
                    state.WorldSleepPoseLogged = true;
                    Camera worldCamera = MainGame.me?.world_cam ?? Camera.main;
                    Vector3 viewport = worldCamera != null
                        ? worldCamera.WorldToViewportPoint(renderer.bounds.center)
                        : Vector3.zero;
                    bool layerVisible = worldCamera != null &&
                        (worldCamera.cullingMask &
                         (1 << renderer.gameObject.layer)) != 0;
                    CoopMod.Logger.LogInfo(
                        $"[SleepSync] Remote world pose: " +
                        $"bed={bedRenderer.sprite?.name ?? "<none>"}/" +
                        $"{bedRenderer.sprite?.pixelsPerUnit ?? 0f:0.##}ppu " +
                        $"bedScale={bedRenderer.transform.lossyScale} " +
                        $"bedBounds={bedBounds.center}/{bedBounds.size}; " +
                        $"sleeper={sleepSprite.name}/" +
                        $"{sleepSprite.pixelsPerUnit:0.##}ppu " +
                        $"poseScale={renderer.transform.lossyScale} " +
                        $"poseBounds={renderer.bounds.center}/" +
                        $"{renderer.bounds.size} layer={renderer.gameObject.layer} " +
                        $"order={renderer.sortingOrder} sourceVisible={visible} " +
                        $"poseActive={state.WorldSleepPose.activeInHierarchy} " +
                        $"rendererEnabled={renderer.enabled} " +
                        $"rendered={renderer.isVisible} " +
                        $"cameraLayer={layerVisible} viewport={viewport} " +
                        $"material={renderer.sharedMaterial?.shader?.name ?? "<none>"}");
                }
                return visible;
            }

            return false;
        }

        private void ApplyRemoteSleepUiPresentation(
            RemoteSleepRendererState state)
        {
            if (state == null)
                return;

            SleepGUI sleepGui = GUIElements.me?.sleep_gui;
            UI2DSprite vanillaBed = sleepGui?.bed_sprite;
            if (sleepGui == null || !sleepGui.is_shown || vanillaBed == null)
            {
                DestroyRemoteSleepUiPresentation(state);
                return;
            }

            if (state.PartySleepGui != null &&
                state.PartySleepGui != sleepGui)
            {
                DestroyRemoteSleepUiPresentation(state);
            }

            int slot = GetRemoteSleepUiSlot(state.SteamId);
            float spacing = Mathf.Max(
                PartyBedMinimumSpacing,
                vanillaBed.width + 12f);
            float offset = GetPartyBedOffset(slot, spacing);

            if (state.PartyBedSprite == null)
            {
                GameObject bedClone = UnityEngine.Object.Instantiate(
                    vanillaBed.gameObject);
                bedClone.name = $"Party Sleep Bed {state.SteamId}";
                bedClone.transform.SetParent(
                    vanillaBed.transform.parent,
                    false);
                bedClone.transform.localRotation =
                    vanillaBed.transform.localRotation;
                bedClone.transform.localScale =
                    vanillaBed.transform.localScale;
                state.PartyBedSprite = bedClone.GetComponent<UI2DSprite>();
                state.PartySleepGui = sleepGui;
            }

            if (state.PartyBedSprite == null)
            {
                DestroyRemoteSleepUiPresentation(state);
                return;
            }

            state.PartyBedSprite.sprite2D = vanillaBed.sprite2D;
            state.PartyBedSprite.width = vanillaBed.width;
            state.PartyBedSprite.height = vanillaBed.height;
            state.PartyBedSprite.depth = vanillaBed.depth;
            state.PartyBedSprite.transform.localPosition =
                vanillaBed.transform.localPosition + Vector3.right * offset;
            state.PartyBedSprite.transform.localRotation =
                vanillaBed.transform.localRotation;
            state.PartyBedSprite.transform.localScale =
                vanillaBed.transform.localScale;
            state.PartyBedSprite.gameObject.SetActive(true);

            UI2DSprite vanillaSleeper = FindVanillaSleepSprite(
                sleepGui,
                vanillaBed);
            bool sleeperIncludedInBedClone =
                vanillaSleeper != null &&
                vanillaSleeper.transform.IsChildOf(vanillaBed.transform);
            if (sleeperIncludedInBedClone)
            {
                if (state.PartySleeperSprite != null)
                {
                    UnityEngine.Object.Destroy(
                        state.PartySleeperSprite.gameObject);
                    state.PartySleeperSprite = null;
                }
                return;
            }

            if (state.PartySleeperSprite == null)
            {
                if (vanillaSleeper != null)
                {
                    GameObject sleeperClone = UnityEngine.Object.Instantiate(
                        vanillaSleeper.gameObject);
                    sleeperClone.name = $"Party Sleeping Keeper {state.SteamId}";
                    sleeperClone.transform.SetParent(
                        vanillaSleeper.transform.parent,
                        false);
                    sleeperClone.transform.localRotation =
                        vanillaSleeper.transform.localRotation;
                    sleeperClone.transform.localScale =
                        vanillaSleeper.transform.localScale;
                    state.PartySleeperSprite =
                        sleeperClone.GetComponent<UI2DSprite>();
                }
                else
                {
                    state.PartySleeperSprite =
                        CreateFallbackPartySleeper(
                            state.SteamId,
                            vanillaBed);
                }
            }

            UI2DSprite partySleeper = state.PartySleeperSprite;
            if (partySleeper == null)
                return;

            if (vanillaSleeper != null)
            {
                partySleeper.sprite2D = vanillaSleeper.sprite2D;
                partySleeper.width = vanillaSleeper.width;
                partySleeper.height = vanillaSleeper.height;
                partySleeper.depth = vanillaSleeper.depth;
                partySleeper.transform.localPosition =
                    vanillaSleeper.transform.localPosition +
                    Vector3.right * offset;
                partySleeper.transform.localRotation =
                    vanillaSleeper.transform.localRotation;
                partySleeper.transform.localScale =
                    vanillaSleeper.transform.localScale;
            }
            else
            {
                PositionFallbackPartySleeper(
                    partySleeper,
                    vanillaBed,
                    offset);
            }
            partySleeper.gameObject.SetActive(true);
        }

        private static SpriteRenderer FindHeroBedRenderer(
            WorldGameObject bed)
        {
            if (bed == null)
                return null;

            // Match the renderer vanilla SleepGUI uses for its bed sprite.
            // The previous largest-bounds heuristic could select an auxiliary
            // child and put both the sleeper and its label far from the pillow.
            SpriteRenderer vanillaRenderer =
                bed.GetComponentInChildren<SpriteRenderer>();
            if (vanillaRenderer?.sprite != null &&
                vanillaRenderer.enabled)
            {
                return vanillaRenderer;
            }

            SpriteRenderer[] renderers =
                bed.GetComponentsInChildren<SpriteRenderer>(true);
            SpriteRenderer best = null;
            float bestArea = -1f;
            for (int i = 0; i < renderers.Length; i++)
            {
                SpriteRenderer candidate = renderers[i];
                if (candidate?.sprite == null || !candidate.enabled ||
                    !candidate.gameObject.activeInHierarchy)
                    continue;

                string spriteName = candidate.sprite.name ?? "";
                if (spriteName.EndsWith(
                        "_sh",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Bounds bounds = candidate.bounds;
                float area = bounds.size.x * bounds.size.y;
                if (best == null || area > bestArea)
                {
                    best = candidate;
                    bestArea = area;
                }
            }
            return best ?? vanillaRenderer;
        }

        private static Sprite GetRemoteSleepSprite()
        {
            if (remoteSleepSprite != null)
                return remoteSleepSprite;

            SleepGUI sleepGui = GUIElements.me?.sleep_gui;
            UI2DSprite vanillaSleeper = FindVanillaSleepSprite(
                sleepGui,
                sleepGui?.bed_sprite);
            if (vanillaSleeper?.sprite2D != null)
            {
                remoteSleepSprite = vanillaSleeper.sprite2D;
                if (!remoteSleepSpriteSourceLogged)
                {
                    remoteSleepSpriteSourceLogged = true;
                    CoopMod.Logger.LogInfo(
                        "[SleepSync] Reusing vanilla SleepGUI keeper sprite for " +
                        "remote sleepers");
                }
                return remoteSleepSprite;
            }

            remoteSleepSprite = EasySpritesCollection.GetSprite(
                RemoteSleepSpriteName,
                true,
                "");
            if (remoteSleepSprite == null && !remoteSleepSpriteMissingLogged)
            {
                remoteSleepSpriteMissingLogged = true;
                CoopMod.Logger.LogWarning(
                    "[SleepSync] Vanilla keeper sleep sprite is unavailable; " +
                    "leaving remote character visible until it loads");
            }
            return remoteSleepSprite;
        }

        private static UI2DSprite FindVanillaSleepSprite(
            SleepGUI sleepGui,
            UI2DSprite vanillaBed)
        {
            if (sleepGui == null)
                return null;

            UI2DSprite[] sprites =
                sleepGui.GetComponentsInChildren<UI2DSprite>(true);
            for (int i = 0; i < sprites.Length; i++)
            {
                UI2DSprite candidate = sprites[i];
                if (candidate == null || candidate == vanillaBed ||
                    candidate.sprite2D == null ||
                    IsPartySleepPresentation(
                        candidate.transform,
                        sleepGui.transform))
                {
                    continue;
                }

                string spriteName = (candidate.sprite2D.name ?? string.Empty)
                    .Replace("(Clone)", string.Empty);
                if (string.Equals(
                        spriteName,
                        RemoteSleepSpriteName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static bool IsPartySleepPresentation(
            Transform candidate,
            Transform sleepGuiRoot)
        {
            for (Transform current = candidate;
                 current != null && current != sleepGuiRoot;
                 current = current.parent)
            {
                if (current.gameObject.name.StartsWith(
                        "Party Sleep",
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static UI2DSprite CreateFallbackPartySleeper(
            ulong steamId,
            UI2DSprite vanillaBed)
        {
            Sprite sleepSprite = GetRemoteSleepSprite();
            if (sleepSprite == null || vanillaBed == null)
                return null;

            var sleeperObject = new GameObject(
                $"Party Sleeping Keeper {steamId}");
            sleeperObject.layer = vanillaBed.gameObject.layer;
            sleeperObject.transform.SetParent(
                vanillaBed.transform.parent,
                false);
            sleeperObject.transform.localRotation =
                vanillaBed.transform.localRotation;
            sleeperObject.transform.localScale =
                vanillaBed.transform.localScale;
            UI2DSprite sleeper = sleeperObject.AddComponent<UI2DSprite>();
            sleeper.sprite2D = sleepSprite;
            sleeper.depth = vanillaBed.depth + 1;

            float bedSpriteWidth = vanillaBed.sprite2D != null
                ? vanillaBed.sprite2D.rect.width
                : vanillaBed.width;
            float scale = bedSpriteWidth > 0f
                ? vanillaBed.width / bedSpriteWidth
                : 1f;
            sleeper.width = Mathf.Max(
                1,
                Mathf.RoundToInt(sleepSprite.rect.width * scale));
            sleeper.height = Mathf.Max(
                1,
                Mathf.RoundToInt(sleepSprite.rect.height * scale));
            return sleeper;
        }

        private static void PositionFallbackPartySleeper(
            UI2DSprite sleeper,
            UI2DSprite vanillaBed,
            float offset)
        {
            if (sleeper == null || vanillaBed == null)
                return;

            sleeper.transform.localPosition =
                vanillaBed.transform.localPosition +
                Vector3.right * offset +
                Vector3.up * (vanillaBed.height * 0.18f);
            sleeper.transform.localRotation =
                vanillaBed.transform.localRotation;
            sleeper.transform.localScale =
                vanillaBed.transform.localScale;
        }

        private int GetRemoteSleepUiSlot(ulong steamId)
        {
            ulong localSteamId = SteamUser.GetSteamID().m_SteamID;
            var remoteIds = new List<ulong>();
            foreach (ulong sleepingId in sleepingPeerRoster)
            {
                if (sleepingId != localSteamId)
                    remoteIds.Add(sleepingId);
            }
            remoteIds.Sort();
            int index = remoteIds.IndexOf(steamId);
            return index >= 0 ? index : 0;
        }

        private static float GetPartyBedOffset(int slot, float spacing)
        {
            if (slot <= 0)
                return spacing;
            if (slot == 1)
                return -spacing;
            return spacing * slot;
        }

        private static void DestroyRemoteSleepWorldPose(
            RemoteSleepRendererState state)
        {
            if (state == null)
                return;

            OnlineCoopManager.Instance?.RestoreRemotePlayerNameTagTarget(
                new CSteamID(state.SteamId),
                state.WorldSleepLabelAnchor);
            if (state.WorldSleepPose != null)
                UnityEngine.Object.Destroy(state.WorldSleepPose);
            state.WorldSleepPose = null;
            state.WorldSleepRenderer = null;
            state.WorldSleepLabelAnchor = null;
        }

        private static void DestroyRemoteSleepUiPresentation(
            RemoteSleepRendererState state)
        {
            if (state == null)
                return;

            if (state.PartyBedSprite != null)
                UnityEngine.Object.Destroy(state.PartyBedSprite.gameObject);
            if (state.PartySleeperSprite != null)
                UnityEngine.Object.Destroy(state.PartySleeperSprite.gameObject);
            state.PartySleepGui = null;
            state.PartyBedSprite = null;
            state.PartySleeperSprite = null;
        }

        private static void ApplyRemoteSleepNameTag(
            RemoteSleepRendererState state,
            WorldGameObject target,
            bool worldPoseVisible)
        {
            if (state == null || target == null)
                return;

            CSteamID steamId = new CSteamID(state.SteamId);
            Transform sleepAnchor = state.WorldSleepLabelAnchor;
            if (worldPoseVisible && sleepAnchor != null)
            {
                OnlineCoopManager.Instance?.SetRemotePlayerNameTagTarget(
                    steamId,
                    sleepAnchor);
            }
            else
            {
                OnlineCoopManager.Instance?.RestoreRemotePlayerNameTagTarget(
                    steamId,
                    sleepAnchor);
            }
        }

        private void RestoreRemoteSleepPresentation(ulong steamId)
        {
            if (!remoteSleepRenderers.TryGetValue(
                    steamId,
                    out RemoteSleepRendererState state))
            {
                return;
            }

            remoteSleepRenderers.Remove(steamId);
            DestroyRemoteSleepWorldPose(state);
            DestroyRemoteSleepUiPresentation(state);

            foreach (RemoteSleepRendererEntry entry in state.Renderers.Values)
            {
                if (entry?.Renderer != null)
                    entry.Renderer.enabled = entry.WasEnabled;
            }

            CoopMod.Logger.LogInfo(
                $"[SleepSync] Restored remote character {steamId} after sleep");
        }

        private void BeginHostWakeCompletionBarrier(
            uint cycle,
            HashSet<ulong> cycleMembers)
        {
            if (wakeCompletionBarrierActive)
            {
                GJCommons.VoidDelegate staleContinuation =
                    deferredHostWakeContinuation;
                CoopMod.Logger.LogWarning(
                    $"[SleepSync] Wake-complete cycle {wakeCompletionCycle} " +
                    "was superseded; releasing its deferred host continuation");
                ClearWakeCompletionBarrier();
                InvokeWakeContinuation(staleContinuation);
            }

            pendingWakeCompletionPeers.Clear();
            if (cycleMembers != null)
            {
                ulong localSteamId = SteamUser.GetSteamID().m_SteamID;
                foreach (ulong member in cycleMembers)
                {
                    if (member != localSteamId)
                        pendingWakeCompletionPeers.Add(member);
                }
            }

            wakeCompletionCycle = cycle;
            wakeCompletionExpectedPeerCount =
                pendingWakeCompletionPeers.Count;
            wakeCompletionBarrierStartedAt = 0f;
            wakeCompletionBarrierActive = true;
            awaitingLocalWakeCompletion = true;
            localWakeTransitionComplete = false;
            deferredHostWakeContinuation = null;
            CoopMod.Logger.LogInfo(
                $"[SleepSync] Started wake-complete barrier for cycle " +
                $"{cycle}; awaiting host + " +
                $"{wakeCompletionExpectedPeerCount} peer(s)");
        }

        private void PrepareLocalWakeCompletion(uint cycle)
        {
            wakeCompletionCycle = cycle;
            awaitingLocalWakeCompletion = true;
            localWakeTransitionComplete = false;
            deferredHostWakeContinuation = null;
        }

        private void SendWakeCompletion(uint cycle)
        {
            using (var writer = new MsgWriter(Op.SleepSync, 8))
            {
                writer.Write(SleepMessageWakeComplete);
                writer.Write(cycle);
                SteamP2PManager.Instance?.SendToHost(writer.ToArray());
            }
            CoopMod.Logger.LogInfo(
                $"[SleepSync] Reported completed local wake transition for " +
                $"cycle {cycle}");
        }

        private void HandleRemoteWakeCompletion(
            CSteamID senderID,
            uint cycle)
        {
            if (!wakeCompletionBarrierActive ||
                cycle != wakeCompletionCycle ||
                !pendingWakeCompletionPeers.Remove(senderID.m_SteamID))
            {
                return;
            }

            int completed = wakeCompletionExpectedPeerCount -
                            pendingWakeCompletionPeers.Count;
            CoopMod.Logger.LogInfo(
                $"[SleepSync] Wake-complete cycle {cycle}: " +
                $"{completed}/{wakeCompletionExpectedPeerCount} peer(s)");
            TryReleaseWakeCompletionBarrier(
                "all wake transitions completed");
        }

        private void TickWakeCompletionBarrier()
        {
            if (!IsHost || !wakeCompletionBarrierActive)
                return;

            bool timedOut = false;
            if (TryGetLobbyMembers(out HashSet<ulong> members) &&
                pendingWakeCompletionPeers.Count > 0)
            {
                var departed = new List<ulong>();
                foreach (ulong pending in pendingWakeCompletionPeers)
                {
                    if (!members.Contains(pending))
                        departed.Add(pending);
                }
                for (int i = 0; i < departed.Count; i++)
                    pendingWakeCompletionPeers.Remove(departed[i]);
                if (departed.Count > 0)
                {
                    CoopMod.Logger.LogInfo(
                        $"[SleepSync] Removed {departed.Count} departed peer(s) " +
                        $"from wake-complete cycle {wakeCompletionCycle}");
                }
            }

            if (localWakeTransitionComplete &&
                pendingWakeCompletionPeers.Count > 0 &&
                Time.realtimeSinceStartup - wakeCompletionBarrierStartedAt >=
                    WakeCompletionTimeoutSeconds)
            {
                CoopMod.Logger.LogWarning(
                    $"[SleepSync] Wake-complete cycle {wakeCompletionCycle} " +
                    $"timed out after {WakeCompletionTimeoutSeconds:F0}s; " +
                    $"continuing without {pendingWakeCompletionPeers.Count} " +
                    "peer acknowledgment(s)");
                pendingWakeCompletionPeers.Clear();
                timedOut = true;
            }

            TryReleaseWakeCompletionBarrier(
                timedOut
                    ? "peer wake-complete timeout"
                    : "all wake transitions completed");
        }

        private void TryReleaseWakeCompletionBarrier(string reason)
        {
            if (!wakeCompletionBarrierActive ||
                !localWakeTransitionComplete ||
                pendingWakeCompletionPeers.Count > 0)
            {
                return;
            }

            uint completedCycle = wakeCompletionCycle;
            GJCommons.VoidDelegate continuation =
                deferredHostWakeContinuation;
            ClearWakeCompletionBarrier();
            CoopMod.Logger.LogInfo(
                $"[SleepSync] Released host wake continuation for cycle " +
                $"{completedCycle} ({reason})");
            InvokeWakeContinuation(continuation);
        }

        private void ClearWakeCompletionBarrier()
        {
            pendingWakeCompletionPeers.Clear();
            wakeCompletionBarrierActive = false;
            awaitingLocalWakeCompletion = false;
            localWakeTransitionComplete = false;
            wakeCompletionCycle = 0U;
            wakeCompletionBarrierStartedAt = 0f;
            wakeCompletionExpectedPeerCount = 0;
            deferredHostWakeContinuation = null;
        }

        private static void InvokeWakeContinuation(
            GJCommons.VoidDelegate continuation)
        {
            try
            {
                continuation?.Invoke();
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[SleepSync] Wake continuation failed: {ex.Message}");
            }
        }

        private void HostWakeEveryone(HashSet<ulong> cycleMembers)
        {
            CoopMod.Logger.LogInfo(
                "[SleepSync] Every player is fully rested; waking the party");
            sleepCycle++;
            BeginHostWakeCompletionBarrier(sleepCycle, cycleMembers);
            int totalPlayers = cycleMembers?.Count ?? 1;
            bool hasFinalClock = MainGame.me?.save != null &&
                                 TimeOfDay.me != null;
            uint finalClockSequence = 0U;
            if (hasFinalClock && SteamP2PManager.Instance != null)
            {
                finalClockSequence =
                    SteamP2PManager.Instance.BroadcastTimeSync(
                        MainGame.me.save.day,
                        TimeOfDay.me.GetTimeK());
            }
            using (var writer = new MsgWriter(Op.SleepSync, 20))
            {
                writer.Write(SleepMessageWakeAll);
                writer.Write(sleepCycle);
                if (hasFinalClock)
                {
                    writer.Write(MainGame.me.save.day);
                    writer.Write(TimeOfDay.me.GetTimeK());
                    if (finalClockSequence != 0U)
                        writer.Write(finalClockSequence);
                }
                SteamP2PManager.Instance?.BroadcastBinary(writer.ToArray());
            }

            hostSleepStates.Clear();
            lastBroadcastSleeping = 0;
            lastBroadcastTotal = totalPlayers;
            lastBroadcastFastForward = false;
            ApplySynchronizedWake();
        }

        private void ApplySynchronizedWake()
        {
            suppressNextSynchronizedFirstSleepStory =
                !IsHost && IsFirstSleepStoryWakeGateActive();
            fastForwardAuthorized = false;
            localSleeping = false;
            localReadyToWake = false;
            firstSleepManualWakeBlockedMessageShown = false;
            RestoreNormalSleepClock();
            ApplySleepingPeerRoster(new HashSet<ulong>());

            SleepGUI sleepGui = GUIElements.me?.sleep_gui;
            if (sleepGui == null || !sleepGui.is_shown)
            {
                suppressNextSynchronizedWakeSpeech = false;
                suppressNextSynchronizedFirstSleepStory = false;
                // There is no remaining sleep presentation to gate. This also
                // makes a duplicate/replayed WakeAll idempotently complete.
                CompleteLocalWakeTransition(null);
                return;
            }

            try
            {
                if (sleepWakeUpMethod == null)
                    sleepWakeUpMethod = AccessTools.Method(typeof(SleepGUI), "WakeUp");

                if (sleepWakeUpMethod == null)
                {
                    suppressNextSynchronizedWakeSpeech = false;
                    suppressNextSynchronizedFirstSleepStory = false;
                    CoopMod.Logger.LogWarning(
                        "[SleepSync] Could not resolve SleepGUI.WakeUp");
                    return;
                }

                suppressNextSynchronizedWakeSpeech = !IsHost;
                systemWakeInProgress = true;
                sleepWakeUpMethod.Invoke(sleepGui, null);
            }
            catch (Exception ex)
            {
                suppressNextSynchronizedWakeSpeech = false;
                suppressNextSynchronizedFirstSleepStory = false;
                CoopMod.Logger.LogWarning($"[SleepSync] Failed to wake local player: {ex.Message}");
            }
            finally
            {
                systemWakeInProgress = false;
            }
        }

        private void ApplySleepClockPolicy()
        {
            if (!localSleeping)
                return;

            sleepClockOwned = true;

            float timeScale = fastForwardAuthorized ? 10f : 1f;
            float fixedDelta = fastForwardAuthorized
                ? 0.083333336f
                : 0.016666668f;

            if (!Mathf.Approximately(Time.timeScale, timeScale))
                Time.timeScale = timeScale;
            if (!Mathf.Approximately(Time.fixedDeltaTime, fixedDelta))
                Time.fixedDeltaTime = fixedDelta;
        }

        private void RestoreNormalSleepClock()
        {
            if (!sleepClockOwned)
                return;

            Time.timeScale = 1f;
            Time.fixedDeltaTime = 0.016666668f;
            sleepClockOwned = false;
        }

        private void ResetSleepCoordinator(bool wakeLocal)
        {
            hostSleepStates.Clear();
            ClearWakeCompletionBarrier();
            ApplySleepingPeerRoster(new HashSet<ulong>());
            fastForwardAuthorized = false;
            localReadyToWake = false;
            sleepCycle = 0;
            lastBroadcastSleeping = -1;
            lastBroadcastTotal = -1;
            lastBroadcastFastForward = false;
            lastAnnouncedSleeping = -1;
            lastAnnouncedTotal = -1;
            unknownSleepRosterTailLogged = false;
            invalidSleepRosterTailLogged = false;
            suppressNextSynchronizedWakeSpeech = false;
            suppressNextSynchronizedFirstSleepStory = false;
            firstSleepManualWakeBlockedMessageShown = false;
            nextSleepRosterCheckAt = 0f;
            nextSleepSnapshotAt = 0f;

            // Also cancel a SleepGUI that is still in its appearing transition;
            // localSleeping is set only after that transition completes.
            if (wakeLocal)
                CancelLocalSleepForTeardown();
            else
                localSleeping = false;

            RestoreNormalSleepClock();
        }

        /// <summary>
        /// Ends the local sleep presentation during session teardown without using
        /// SleepGUI.WakeUp. Vanilla WakeUp always autosaves, which would overwrite a
        /// joiner's downloaded host-save mirror with client-local world state just as
        /// the host connection is disappearing.
        /// </summary>
        private void CancelLocalSleepForTeardown()
        {
            localSleeping = false;
            localReadyToWake = false;
            fastForwardAuthorized = false;
            systemWakeInProgress = false;
            suppressNextSynchronizedWakeSpeech = false;
            suppressNextSynchronizedFirstSleepStory = false;
            firstSleepManualWakeBlockedMessageShown = false;
            RestoreNormalSleepClock();

            SleepGUI sleepGui = GUIElements.me?.sleep_gui;
            if (sleepGui == null || !sleepGui.is_shown)
                return;

            try
            {
                sleepGui.button_tips?.Clear();
                sleepGui.Hide(false);
                CoopMod.Logger.LogInfo(
                    "[SleepSync] Cancelled local sleep during session teardown " +
                    "without autosaving the host-save mirror");
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    "[SleepSync] Failed to close local sleep presentation during " +
                    $"session teardown: {ex.Message}");
            }
        }

        private static bool TryGetLobbyMembers(out HashSet<ulong> members)
        {
            members = new HashSet<ulong>();
            CSteamID lobby = SteamLobbyManager.Instance?.CurrentLobbyID ?? CSteamID.Nil;
            if (lobby == CSteamID.Nil)
                return false;

            int count = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < count; i++)
            {
                CSteamID member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
                if (member != CSteamID.Nil)
                    members.Add(member.m_SteamID);
            }
            return members.Count > 0;
        }

        private static bool IsExpectedHost(CSteamID senderID)
        {
            CSteamID lobby = SteamLobbyManager.Instance?.CurrentLobbyID ?? CSteamID.Nil;
            return lobby != CSteamID.Nil &&
                   SteamMatchmaking.GetLobbyOwner(lobby) == senderID;
        }

        private void OnTimeSyncReceived(CSteamID senderID, int day, float timeK)
        {
            if (!IsSyncEnabled) return;
            if (IsHost) return;
            if (!IsExpectedHost(senderID)) return;
            if (MainGame.me == null || MainGame.me.save == null || TimeOfDay.me == null) return;

            float skewMinutes = Mathf.Abs(
                ((day - MainGame.me.save.day) +
                 (timeK - TimeOfDay.me.GetTimeK())) * 24f * 60f);
            if (skewMinutes >= ClockSkewDiagnosticMinutes &&
                Time.realtimeSinceStartup >= nextClockSkewDiagnosticAt)
            {
                nextClockSkewDiagnosticAt =
                    Time.realtimeSinceStartup +
                    ClockSkewDiagnosticIntervalSeconds;
                CoopMod.Logger.LogWarning(
                    $"[GameTimeSync] Correcting client clock skew of " +
                    $"{skewMinutes:F1} in-game minutes " +
                    $"(local={MainGame.me.save.day}:{TimeOfDay.me.GetTimeK():F5}, " +
                    $"host={day}:{timeK:F5})");
            }

            ApplyAuthoritativeTime(day, timeK);
        }

        public static void ApplyAuthoritativeTime(int day, float timeK)
        {
            if (MainGame.me == null || MainGame.me.save == null || TimeOfDay.me == null) return;

            int localDay = MainGame.me.save.day;
            if (day > localDay && EnvironmentEngine.me != null)
            {
                int daysToReplay = Mathf.Min(day - localDay, MaxCatchUpDaysPerPacket);
                MethodInfo method = GetOnEndOfDayMethod();

                for (int i = 0; i < daysToReplay; i++)
                {
                    if (method == null)
                    {
                        MainGame.me.save.day++;
                    }
                    else
                    {
                        try
                        {
                            method.Invoke(EnvironmentEngine.me, null);
                        }
                        catch (Exception ex)
                        {
                            CoopMod.Logger.LogWarning($"[GameTimeSync] Failed to replay end-of-day side effects: {ex.Message}");
                            MainGame.me.save.day++;
                            break;
                        }
                    }
                }

                if (MainGame.me.save.day != day)
                {
                    MainGame.me.save.day = day;
                }
            }
            else if (MainGame.me.save.day != day)
            {
                MainGame.me.save.day = day;
            }

            TimeOfDay.me.SetTimeK(timeK);

            try
            {
                EnvironmentEngine.me?.UpdateWeather();
                GUIElements.me?.hud?.Update();
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[GameTimeSync] Failed to refresh time-dependent UI/environment: {ex.Message}");
            }
        }

        private static MethodInfo GetOnEndOfDayMethod()
        {
            if (onEndOfDayMethod == null)
            {
                onEndOfDayMethod = AccessTools.Method(typeof(EnvironmentEngine), "OnEndOfDay");
            }
            return onEndOfDayMethod;
        }

        private static void DisableClientAutoTime()
        {
            try
            {
                EnvironmentEngine.me?.DisableAutoTime();
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[GameTimeSync] Failed to disable client auto time: {ex.Message}");
            }
        }

        public static bool ParseTimeSyncMessage(string message, out int day, out float timeK)
        {
            day = 0;
            timeK = 0f;

            if (!message.StartsWith(MSG_TIME_SYNC)) return false;

            try
            {
                string data = message.Substring(MSG_TIME_SYNC.Length);
                string[] parts = data.Split(',');

                if (parts.Length >= 2)
                {
                    day = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    timeK = float.Parse(parts[1], CultureInfo.InvariantCulture);
                    return true;
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[GameTimeSync] Failed to parse time sync message: {ex.Message}");
            }

            return false;
        }

        public static bool IsTimeSyncMessage(string message)
        {
            return message.StartsWith(MSG_TIME_SYNC);
        }
    }
}

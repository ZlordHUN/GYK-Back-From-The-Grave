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
    /// Host broadcasts time at 0.5 Hz (every 2 seconds).
    /// Clients receive and apply the time to stay in sync.
    /// </summary>
    public class GameTimeSync : SyncBehaviour
    {
        public static GameTimeSync Instance => GetInstance<GameTimeSync>();

        public const string MSG_TIME_SYNC = "TIME:";

        private const float SYNC_HZ = 0.5f;
        private const float SleepRosterCheckSeconds = 0.5f;
        private const float SleepSnapshotRepeatSeconds = 2f;
        private const byte SleepMessageState = 0;
        private const byte SleepMessageSnapshot = 1;
        private const byte SleepMessageWakeAll = 2;
        private const int MaxCatchUpDaysPerPacket = 14;
        private float lastSendTime = 0f;
        private float nextSleepRosterCheckAt;
        private float nextSleepSnapshotAt;
        private static MethodInfo onEndOfDayMethod;
        private static MethodInfo sleepWakeUpMethod;

        private sealed class SleepPeerState
        {
            public bool Sleeping;
            public bool ReadyToWake;
        }

        private readonly Dictionary<ulong, SleepPeerState> hostSleepStates =
            new Dictionary<ulong, SleepPeerState>();
        private bool localSleeping;
        private bool localReadyToWake;
        private bool fastForwardAuthorized;
        private bool systemWakeInProgress;
        private bool sleepClockOwned;
        private uint sleepCycle;
        private int lastBroadcastSleeping = -1;
        private int lastBroadcastTotal = -1;
        private bool lastBroadcastFastForward;
        private int lastAnnouncedSleeping = -1;
        private int lastAnnouncedTotal = -1;

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

            if (Time.realtimeSinceStartup - lastSendTime < (1f / SYNC_HZ)) return;

            lastSendTime = Time.realtimeSinceStartup;
            SendTimeSync();
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

        /// <summary>Called after the local vanilla SleepGUI successfully opens.</summary>
        public void NotifyLocalSleepStarted()
        {
            if (!IsSyncEnabled || !IsOnline || localSleeping)
                return;

            localSleeping = true;
            localReadyToWake = false;
            fastForwardAuthorized = false;
            ApplySleepClockPolicy();

            CoopMod.Logger.LogInfo("[SleepSync] Local player entered bed");
            SubmitLocalSleepState();
        }

        /// <summary>
        /// Called before vanilla SleepGUI.WakeUp. Automatic wake is held until every
        /// sleeping player is restored; an explicit interaction/back press always lets
        /// that player leave bed and withdraws their sleep vote.
        /// </summary>
        public bool HandleLocalWakeAttempt(bool manualWake)
        {
            if (!IsSyncEnabled || !IsOnline || !localSleeping || systemWakeInProgress)
                return true;

            if (manualWake)
            {
                CoopMod.Logger.LogInfo("[SleepSync] Local player left bed manually");
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
                    ApplySleepSnapshot(sleeping, total, fastForward, cycle);
                }
                else if (messageType == SleepMessageWakeAll)
                {
                    if (reader.Remaining < 4)
                        return;

                    sleepCycle = reader.ReadUInt32();
                    ApplySynchronizedWake();
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[SleepSync] Failed to process sleep message: {ex.Message}");
            }
        }

        public void NotifySleepPeerLeft(CSteamID peer)
        {
            if (!IsSyncEnabled || !IsHost || peer == CSteamID.Nil)
                return;

            if (hostSleepStates.Remove(peer.m_SteamID))
                EvaluateHostSleepState(forceBroadcast: true);
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
                forceBroadcast);

            if (allSleeping && allReadyToWake)
                HostWakeEveryone(members.Count);
        }

        private void BroadcastSleepSnapshot(
            int sleeping,
            int total,
            bool fastForward,
            bool force)
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
            ApplySleepSnapshot(sleeping, total, fastForward, sleepCycle);

            using (var writer = new MsgWriter(Op.SleepSync, 20))
            {
                writer.Write(SleepMessageSnapshot);
                writer.Write(sleeping);
                writer.Write(total);
                writer.Write(fastForward);
                writer.Write(sleepCycle);
                SteamP2PManager.Instance?.BroadcastBinary(writer.ToArray());
            }
        }

        private void ApplySleepSnapshot(
            int sleeping,
            int total,
            bool fastForward,
            uint cycle)
        {
            if (sleeping < 0 || total <= 0 || sleeping > total)
                return;

            sleepCycle = cycle;
            fastForwardAuthorized = fastForward && localSleeping;
            ApplySleepClockPolicy();

            if (sleeping != lastAnnouncedSleeping || total != lastAnnouncedTotal)
            {
                lastAnnouncedSleeping = sleeping;
                lastAnnouncedTotal = total;
                ChatManager.AddMessage($"[System] {sleeping}/{total} players asleep");
            }
        }

        private void HostWakeEveryone(int totalPlayers)
        {
            CoopMod.Logger.LogInfo(
                "[SleepSync] Every player is fully rested; waking the party");
            sleepCycle++;
            using (var writer = new MsgWriter(Op.SleepSync, 8))
            {
                writer.Write(SleepMessageWakeAll);
                writer.Write(sleepCycle);
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
            fastForwardAuthorized = false;
            localSleeping = false;
            localReadyToWake = false;
            RestoreNormalSleepClock();

            SleepGUI sleepGui = GUIElements.me?.sleep_gui;
            if (sleepGui == null || !sleepGui.is_shown)
                return;

            try
            {
                if (sleepWakeUpMethod == null)
                    sleepWakeUpMethod = AccessTools.Method(typeof(SleepGUI), "WakeUp");

                systemWakeInProgress = true;
                sleepWakeUpMethod?.Invoke(sleepGui, null);
            }
            catch (Exception ex)
            {
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
            bool shouldWake = wakeLocal && localSleeping;
            hostSleepStates.Clear();
            fastForwardAuthorized = false;
            localReadyToWake = false;
            sleepCycle = 0;
            lastBroadcastSleeping = -1;
            lastBroadcastTotal = -1;
            lastBroadcastFastForward = false;
            lastAnnouncedSleeping = -1;
            lastAnnouncedTotal = -1;
            nextSleepRosterCheckAt = 0f;
            nextSleepSnapshotAt = 0f;

            if (shouldWake)
                ApplySynchronizedWake();
            else
                localSleeping = false;

            RestoreNormalSleepClock();
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
            if (MainGame.me == null || MainGame.me.save == null || TimeOfDay.me == null) return;

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

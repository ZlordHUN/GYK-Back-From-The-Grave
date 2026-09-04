using System;
using System.Collections.Generic;
using GraveyardKeeperCoop.Network;
using Steamworks;
using UnityEngine;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Keeps the vanilla saving ledger visible for the full duration of a
    /// mod-initiated manual save and mirrors that state to every lobby peer.
    /// </summary>
    public static class ManualSaveIndicatorSync
    {
        private const float SaveActivityTimeoutSeconds = 180f;

        private static readonly Dictionary<ulong, float> RemoteSaveStartedAt =
            new Dictionary<ulong, float>();
        private static readonly List<ulong> ExpiredPeers = new List<ulong>();

        private static int localSaveDepth;
        private static float localSaveStartedAt;
        private static bool ownsIndicator;

        public static void BeginLocal()
        {
            localSaveDepth++;
            localSaveStartedAt = Time.realtimeSinceStartup;

            if (localSaveDepth == 1)
            {
                CoopMod.Logger.LogInfo("[ManualSaveIndicator] Local manual save started");
                Broadcast(true);
            }

            RefreshIndicator();
        }

        public static void EndLocal()
        {
            if (localSaveDepth <= 0)
            {
                CoopMod.Logger.LogWarning(
                    "[ManualSaveIndicator] Ignored unmatched local save completion");
                return;
            }

            localSaveDepth--;
            if (localSaveDepth == 0)
            {
                CoopMod.Logger.LogInfo("[ManualSaveIndicator] Local manual save completed");
                Broadcast(false);
            }

            RefreshIndicator();
        }

        public static void HandleRemote(CSteamID sender, ref MsgReader reader)
        {
            if (sender == CSteamID.Nil || reader.Remaining < 1)
            {
                CoopMod.Logger.LogWarning(
                    $"[ManualSaveIndicator] Dropped malformed save activity from {sender}");
                return;
            }

            bool active = reader.ReadBool();
            if (active)
            {
                RemoteSaveStartedAt[sender.m_SteamID] = Time.realtimeSinceStartup;
                CoopMod.Logger.LogInfo(
                    $"[ManualSaveIndicator] Peer {sender} started a manual save");
            }
            else
            {
                RemoteSaveStartedAt.Remove(sender.m_SteamID);
                CoopMod.Logger.LogInfo(
                    $"[ManualSaveIndicator] Peer {sender} completed a manual save");
            }

            RefreshIndicator();
        }

        public static void Update()
        {
            float now = Time.realtimeSinceStartup;
            bool stateChanged = false;

            if (localSaveDepth > 0 &&
                now - localSaveStartedAt >= SaveActivityTimeoutSeconds)
            {
                CoopMod.Logger.LogWarning(
                    "[ManualSaveIndicator] Local save activity timed out; clearing indicator");
                localSaveDepth = 0;
                Broadcast(false);
                stateChanged = true;
            }

            ExpiredPeers.Clear();
            foreach (KeyValuePair<ulong, float> entry in RemoteSaveStartedAt)
            {
                if (now - entry.Value >= SaveActivityTimeoutSeconds)
                    ExpiredPeers.Add(entry.Key);
            }

            for (int i = 0; i < ExpiredPeers.Count; i++)
            {
                ulong peer = ExpiredPeers[i];
                RemoteSaveStartedAt.Remove(peer);
                CoopMod.Logger.LogWarning(
                    $"[ManualSaveIndicator] Peer {peer} save activity timed out");
                stateChanged = true;
            }

            if (stateChanged || IsAnySaveActive())
                RefreshIndicator();
        }

        public static void OnPeerLeftLobby(CSteamID peer)
        {
            if (peer != CSteamID.Nil && RemoteSaveStartedAt.Remove(peer.m_SteamID))
                RefreshIndicator();
        }

        public static void Reset()
        {
            bool hadActivity = localSaveDepth > 0 || RemoteSaveStartedAt.Count > 0;
            localSaveDepth = 0;
            localSaveStartedAt = 0f;
            RemoteSaveStartedAt.Clear();
            ExpiredPeers.Clear();

            if (hadActivity || ownsIndicator)
                RefreshIndicator();
        }

        private static bool IsAnySaveActive()
        {
            return localSaveDepth > 0 || RemoteSaveStartedAt.Count > 0;
        }

        private static void RefreshIndicator()
        {
            bool shouldShow = IsAnySaveActive();
            GUIElements gui = GUIElements.me;
            DiskIndicatorGUI indicator = gui?.disk_indicator;

            if (indicator == null)
                return;

            if (shouldShow)
            {
                ownsIndicator = true;
                if (!indicator.gameObject.activeSelf)
                    gui.ShowSavingStatus(true);
            }
            else if (ownsIndicator)
            {
                // PlatformSpecific also hides the indicator immediately before its
                // callback. Calling this again is harmless and restores a remote
                // save if another peer is still active.
                gui.ShowSavingStatus(false);
                ownsIndicator = false;
            }
        }

        private static void Broadcast(bool active)
        {
            if (!SteamManager.Initialized ||
                SteamLobbyManager.Instance?.IsInLobby != true)
            {
                return;
            }

            try
            {
                using (var writer = new MsgWriter(Op.ManualSaveActivity, 2))
                {
                    writer.Write(active);
                    SteamP2PManager.Instance.BroadcastBinary(
                        writer.ToArray(),
                        EP2PSend.k_EP2PSendReliable);
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning(
                    $"[ManualSaveIndicator] Failed to broadcast save activity: {ex.Message}");
            }
        }
    }
}

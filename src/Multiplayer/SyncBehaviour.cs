using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Steamworks;
using UnityEngine;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Base class for all network sync components. Provides:
    /// - Singleton lifecycle (Awake, DontDestroyOnLoad)
    /// - SteamP2PManager event subscribe/unsubscribe
    /// - Sequence deduplication (lastReceivedSequenceByOrigin)
    /// - Online-coop guard helpers
    /// - Enable/disable lifecycle
    /// </summary>
    public abstract class SyncBehaviour : MonoBehaviour
    {
        private static readonly Dictionary<Type, SyncBehaviour> _instances = new Dictionary<Type, SyncBehaviour>();

        public static T GetInstance<T>() where T : SyncBehaviour
        {
            return _instances.TryGetValue(typeof(T), out var inst) ? (T)inst : null;
        }

        private readonly Dictionary<ulong, uint> _lastReceivedSequenceByOrigin = new Dictionary<ulong, uint>();

        protected bool IsSyncEnabled { get; private set; }
        protected float SuppressSendUntil { get; set; }
        protected uint NextSequence;

        protected virtual float EchoSuppressSeconds => 0.5f;

        /// <summary>
        /// Log prefix used for all log messages (e.g. "[DungeonSync]").
        /// </summary>
        protected abstract string LogPrefix { get; }

        private void Awake()
        {
            var type = GetType();
            if (_instances.TryGetValue(type, out var existing) && existing != null && existing != this)
            {
                Destroy(gameObject);
                return;
            }
            _instances[type] = this;
            DontDestroyOnLoad(gameObject);
            CoopMod.Logger.LogInfo($"{LogPrefix} Initialized");
            OnAwake();
        }

        /// <summary>Called after base Awake completes. Override for additional init.</summary>
        protected virtual void OnAwake() { }

        private void OnEnable()
        {
            SubscribeEvents();
        }

        private void OnDisable()
        {
            UnsubscribeEvents();
        }

        /// <summary>Subscribe to SteamP2PManager events. Called in OnEnable.</summary>
        protected abstract void SubscribeEvents();

        /// <summary>Unsubscribe from SteamP2PManager events. Called in OnDisable.</summary>
        protected abstract void UnsubscribeEvents();

        /// <summary>Called by EnableSync — resets sync state and clears sequences.</summary>
        protected virtual void OnSyncEnabled() { }

        /// <summary>Called by DisableSync — clears sequences.</summary>
        protected virtual void OnSyncDisabled() { }

        public void EnableSync()
        {
            IsSyncEnabled = true;
            SuppressSendUntil = 0f;
            NextSequence = 0;
            _lastReceivedSequenceByOrigin.Clear();
            OnSyncEnabled();
            CoopMod.Logger.LogInfo($"{LogPrefix} Sync enabled");
        }

        public void DisableSync()
        {
            IsSyncEnabled = false;
            _lastReceivedSequenceByOrigin.Clear();
            OnSyncDisabled();
            CoopMod.Logger.LogInfo($"{LogPrefix} Sync disabled");
        }

        // ─── Online guard helpers ───────────────────────────────────────

        protected bool IsOnline => OnlineCoopManager.Instance != null
            && OnlineCoopManager.Instance.IsOnlineCoopEnabled;

        protected bool IsHost => OnlineCoopManager.Instance != null
            && OnlineCoopManager.Instance.IsOnlineCoopEnabled
            && OnlineCoopManager.Instance.IsHost;

        // ─── Sequence deduplication ─────────────────────────────────────

        /// <summary>
        /// Reads a uint sequence and CSteamID from the reader, checks for
        /// stale/duplicate messages, and records the sequence. Returns true
        /// if the message should be processed.
        /// </summary>
        protected bool CheckSequence(BinaryReader reader, CSteamID senderID)
        {
            uint seq = reader.ReadUInt32();
            ulong originId = senderID.m_SteamID;
            if (_lastReceivedSequenceByOrigin.TryGetValue(originId, out uint lastSeq) && seq <= lastSeq)
                return false;
            _lastReceivedSequenceByOrigin[originId] = seq;
            return true;
        }

        /// <summary>
        /// Reads sequence from reader (no senderID variant — uses a provided origin).
        /// </summary>
        protected bool CheckSequence(BinaryReader reader, ulong originId)
        {
            uint seq = reader.ReadUInt32();
            if (_lastReceivedSequenceByOrigin.TryGetValue(originId, out uint lastSeq) && seq <= lastSeq)
                return false;
            _lastReceivedSequenceByOrigin[originId] = seq;
            return true;
        }

        protected void ClearSequences() => _lastReceivedSequenceByOrigin.Clear();

        protected bool TryReadPayload(byte[] payload, byte expectedVersion, Action<BinaryReader> readPayload, string errorContext = "message")
        {
            if (!IsSyncEnabled) return false;
            if (payload == null || payload.Length == 0) return false;
            if (!IsOnline) return false;
            if (readPayload == null) return false;

            try
            {
                using (var stream = new MemoryStream(payload))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    byte version = reader.ReadByte();
                    if (version != expectedVersion)
                    {
                        CoopMod.Logger.LogWarning($"{LogPrefix} Unknown payload version {version}, expected {expectedVersion}");
                        return false;
                    }

                    readPayload(reader);
                    return true;
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Error processing {errorContext}: {ex.Message}");
                return false;
            }
        }

        protected bool TryReadPayloadWithSubtype(byte[] payload, byte expectedVersion, Action<BinaryReader, byte> readPayload, string errorContext = "message")
        {
            if (readPayload == null) return false;

            return TryReadPayload(payload, expectedVersion, reader =>
            {
                byte subType = reader.ReadByte();
                readPayload(reader, subType);
            }, errorContext);
        }

        // ─── Host echo suppress on receive ──────────────────────────────

        protected void ApplyEchoSuppress()
        {
            if (OnlineCoopManager.Instance?.IsHost == true)
                SuppressSendUntil = Time.realtimeSinceStartup + EchoSuppressSeconds;
        }

        protected bool IsSuppressed => Time.realtimeSinceStartup < SuppressSendUntil;

        protected bool IsApplyingRemoteChange;

        public static bool GlobalRemoteApplyActive { get; private set; }

        private static float _globalRemoteApplyCooldownUntil;

        public static bool GlobalRemoteApplyCooldown => Time.realtimeSinceStartup < _globalRemoteApplyCooldownUntil;

        protected void ApplyRemote(Action action)
        {
            IsApplyingRemoteChange = true;
            var wasGlobal = GlobalRemoteApplyActive;
            GlobalRemoteApplyActive = true;
            try { action(); }
            finally
            {
                IsApplyingRemoteChange = false;
                GlobalRemoteApplyActive = wasGlobal;
                _globalRemoteApplyCooldownUntil = Time.realtimeSinceStartup + 3f;
            }
        }
        }

        /// <summary>
        /// Base for sync components that periodically snapshot and broadcast state.
    /// Adds: interval throttling, fingerprint-based change detection, force-next-send.
    /// </summary>
    public abstract class PeriodicSyncBehaviour : SyncBehaviour
    {
        protected abstract float SyncIntervalSeconds { get; }
        protected string LastSentFingerprint = string.Empty;
        protected bool ForceNextSend;
        private float _lastSendTime;

        protected sealed override void OnSyncEnabled()
        {
            _lastSendTime = -SyncIntervalSeconds;
            LastSentFingerprint = string.Empty;
            ForceNextSend = true;
            OnPeriodicSyncEnabled();
        }

        protected virtual void OnPeriodicSyncEnabled() { }

        private void Update()
        {
            var __sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!IsSyncEnabled) return;
                if (!IsOnline) return;
                if (!ShouldUpdate()) return;
                if (GlobalRemoteApplyActive) return;

                float now = Time.realtimeSinceStartup;
                if (now - _lastSendTime < SyncIntervalSeconds && !ForceNextSend) return;
                if (IsSuppressed) return;

                SendLocalSnapshot(force: ForceNextSend);
            }
            finally
            {
                GraveyardKeeperCoop.Utils.FrameProfiler.Record("Periodic.Update", __sw.ElapsedTicks);
            }
        }

        /// <summary>Override to add extra Update guard conditions (e.g. game_started).</summary>
        protected virtual bool ShouldUpdate() => true;

        public virtual void SendLocalSnapshot(bool force)
        {
            if (!IsOnline) return;
            if (!ShouldSend()) return;
            if (!CanSendPeriodicSnapshot) return;

            byte[] payload;
            string fingerprint;

            try
            {
                payload = CaptureSnapshot(out fingerprint);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"{LogPrefix} Failed to capture snapshot: {ex.Message}");
                return;
            }

            if (payload == null || payload.Length == 0)
            {
                _lastSendTime = Time.realtimeSinceStartup;
                ForceNextSend = false;
                return;
            }
            if (!force && fingerprint == LastSentFingerprint)
            {
                _lastSendTime = Time.realtimeSinceStartup;
                ForceNextSend = false;
                return;
            }

            LastSentFingerprint = fingerprint;
            _lastSendTime = Time.realtimeSinceStartup;
            ForceNextSend = false;

            BroadcastPayload(payload);
        }

        /// <summary>
        /// Periodic snapshots are host-canonical by default. State systems whose
        /// mutations can originate on clients may opt into client-to-host sends.
        /// </summary>
        protected virtual bool CanSendPeriodicSnapshot => IsHost;

        /// <summary>Override to add extra send guard conditions (e.g. game_started, save != null).</summary>
        protected virtual bool ShouldSend() => true;

        /// <summary>Capture current state into a payload bytes and fingerprint string.</summary>
        protected abstract byte[] CaptureSnapshot(out string fingerprint);

        /// <summary>Send the payload over the network.</summary>
        protected abstract void BroadcastPayload(byte[] payload);

        /// <summary>Mark that state has changed and the next update should force-send.</summary>
        protected internal void MarkDirty()
        {
            if (!IsSyncEnabled || !IsOnline) return;
            ForceNextSend = true;
        }
    }
}

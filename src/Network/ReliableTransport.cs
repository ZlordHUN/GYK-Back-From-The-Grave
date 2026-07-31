using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using Steamworks;
using UnityEngine;

namespace GraveyardKeeperCoop.Network
{
    public enum ReliableSendDisposition
    {
        Native,
        Handled,
        Rejected
    }

    /// <summary>
    /// Application-level reliable delivery over Steam's unreliable datagrams.
    /// Routes are negotiated once per peer and lobby, then remain immutable.
    /// </summary>
    public sealed class ReliableTransport
    {
        // Use a distinct key from the public v1 transport. That implementation
        // treats every non-empty "rnet" value as compatible, so reusing it for a
        // different wire format would create an asymmetric mixed-version route.
        public const string CapabilityKey = "rnet2";
        public const string CapabilityValue = "2";
        public const string NonceKey = "rnet2_nonce";
        public const string LegacyCapabilityKey = "rnet";

        private const byte TagEnvelope = 0xFE;
        private const byte TagAck = 0xFF;
        private const byte WireVersion = 2;
        private const byte KindHello = 1;
        private const byte KindHelloAck = 2;
        private const byte KindData = 3;
        private const byte KindCommit = 4;
        private const byte KindFallbackRequest = 5;
        private const byte KindFallbackAck = 6;
        private const byte KindFallbackCommit = 7;
        private const byte KindFallbackCommitAck = 8;

        private const int EnvelopeHeaderBytes = 28;
        private const int HelloBytes = 36;
        private const int HelloAckBytes = 44;
        private const int CommitBytes = 36;
        private const int FallbackBytes = 36;
        private const int FallbackAckBytes = 44;
        private const int DataHeaderBytes = 33;
        private const int AckBytes = 31;
        private const int FragmentPayloadBytes = 1050;
        private const int MaxLogicalMessageBytes = 8 * 1024 * 1024;
        private const int MaxLobbyLaneMessageBytes = 256 * 1024;
        private const int MaxPendingMessages = 512;
        private const int MaxPendingBytes = 16 * 1024 * 1024;
        // Must remain comfortably below SaveTransferManager's first 5 second retry.
        private const float NativeDecisionDelay = 2f;
        private const float HelloInterval = 0.5f;
        private const float FallbackCommitPhase = 2f;
        private const float FallbackResponderLifetime = 6f;
        private const float HeartbeatInterval = 2f;
        private const float RawPeerFreshWindow = 5f;
        private const float DegradedWarningAfter = 15f;

        public static float ChaosDropRate;
        internal static readonly System.Random ChaosRng = new System.Random();

        private enum RouteState
        {
            Unknown,
            Probing,
            Channel,
            Native
        }

        private sealed class PendingMessage
        {
            public int Lane;
            public byte[] Data;
        }

        private sealed class PeerState
        {
            public CSteamID Peer;
            public RouteState Route;
            public ulong RemoteNonce;
            public bool HasRemoteNonce;
            public bool MetadataConfirmed;
            public ulong LocalChallenge;
            public ulong RemoteChallenge;
            public bool HasRemoteChallenge;
            public bool HandshakeObserved;
            public bool FallbackPending;
            public bool FallbackInitiated;
            public bool FallbackCommitPending;
            public bool FallbackResponsePending;
            public ulong RemoteFallbackChallenge;
            public float NextFallbackAt;
            public float FallbackResponseExpiresAt;
            public float FallbackCommitExpiresAt;
            public float NativeDecisionAt;
            public float NextHelloAt;
            public float NextHeartbeatAt;
            public bool DegradedWarned;
            public readonly Queue<PendingMessage> Pending = new Queue<PendingMessage>();
            public int PendingBytes;
            public readonly Dictionary<int, ReliableLane> Lanes = new Dictionary<int, ReliableLane>();
        }

        private readonly Dictionary<ulong, PeerState> peers = new Dictionary<ulong, PeerState>();
        private readonly List<PeerState> peerTickSnapshot = new List<PeerState>();
        private readonly SteamP2PManager manager;
        private CSteamID lobbyID = CSteamID.Nil;
        private ulong localNonce;

        public string LocalNonceValue => localNonce.ToString("X16", CultureInfo.InvariantCulture);

        public ReliableTransport(SteamP2PManager manager)
        {
            this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
        }

        public void BeginLobby(CSteamID newLobbyID)
        {
            if (newLobbyID == CSteamID.Nil)
            {
                Clear();
                return;
            }

            if (lobbyID == newLobbyID && localNonce != 0)
                return;

            peers.Clear();
            lobbyID = newLobbyID;
            localNonce = CreateNonce();
            CoopMod.Logger.LogInfo($"[RNET] Started transport session for lobby {lobbyID}");
        }

        public void Clear()
        {
            peers.Clear();
            peerTickSnapshot.Clear();
            lobbyID = CSteamID.Nil;
            localNonce = 0;
        }

        public void OnPeerEntered(CSteamID peer)
        {
            if (!CanOwnPeer(peer))
                return;

            PeerState state = GetOrCreatePeer(peer);
            TryStartProbe(state, Time.realtimeSinceStartup);
        }

        public void RemovePeer(CSteamID peer)
        {
            if (peer != CSteamID.Nil)
                peers.Remove(peer.m_SteamID);
        }

        public ReliableSendDisposition RouteSend(CSteamID peer, byte[] data, int lane)
        {
            if (!CanOwnPeer(peer) || data == null || data.Length == 0 ||
                !IsSupportedLane(lane) || data.Length > GetMaxMessageBytes(lane))
            {
                return ReliableSendDisposition.Rejected;
            }

            float now = Time.realtimeSinceStartup;
            PeerState state = GetOrCreatePeer(peer);
            TryStartProbe(state, now);

            if (state.Route == RouteState.Native && state.Pending.Count == 0)
                return ReliableSendDisposition.Native;

            if (state.Route == RouteState.Channel && state.Pending.Count == 0)
            {
                ReliableLane channel = GetOrCreateLane(state, lane);
                if (channel == null || !channel.Send(data, now))
                    return ReliableSendDisposition.Rejected;
                return ReliableSendDisposition.Handled;
            }

            if (!EnqueuePending(state, data, lane))
                return ReliableSendDisposition.Rejected;

            if (state.Route == RouteState.Native)
                FlushPendingNative(state);
            else if (state.Route == RouteState.Channel)
                FlushPendingChannel(state, now);

            return ReliableSendDisposition.Handled;
        }

        /// <summary>
        /// Called only for packets whose opcode/lane is known to use native reliable delivery.
        /// Normal native-unreliable gameplay packets must bypass this method.
        /// </summary>
        public bool AcceptNativeInbound(CSteamID peer)
        {
            if (lobbyID == CSteamID.Nil || !manager.IsCurrentLobbyMember(peer))
                return false;

            PeerState state = GetOrCreatePeer(peer);
            if (state.Route == RouteState.Channel ||
                (state.Route == RouteState.Probing &&
                 (state.HandshakeObserved || state.FallbackPending)))
            {
                CoopMod.Logger.LogWarning($"[RNET] Dropped mixed native-reliable packet from {PeerName(peer)}");
                return false;
            }

            if (state.Route == RouteState.Unknown || state.Route == RouteState.Probing)
                LockNative(state, "received native reliable traffic");

            return true;
        }

        public bool HandleRawIncoming(CSteamID sender, byte[] data, int length)
        {
            if (data == null || length < 1)
                return false;

            byte tag = data[0];
            if (tag != TagEnvelope && tag != TagAck)
                return false;

            // A recognized transport tag is always consumed, including malformed/stale frames.
            if (lobbyID == CSteamID.Nil || localNonce == 0 || !manager.IsCurrentLobbyMember(sender))
                return true;

            if (tag == TagEnvelope)
                HandleEnvelope(sender, data, length);
            else
                HandleAck(sender, data, length);
            return true;
        }

        public void Tick()
        {
            if (lobbyID == CSteamID.Nil || peers.Count == 0)
                return;

            float now = Time.realtimeSinceStartup;
            peerTickSnapshot.Clear();
            peerTickSnapshot.AddRange(peers.Values);
            for (int i = 0; i < peerTickSnapshot.Count; i++)
            {
                PeerState state = peerTickSnapshot[i];
                if (!peers.ContainsKey(state.Peer.m_SteamID))
                    continue;

                if (state.Route == RouteState.Unknown)
                {
                    TryStartProbe(state, now);
                    if (state.Route == RouteState.Unknown && now >= state.NativeDecisionAt)
                        LockNative(state, "no compatible capability received");
                }

                if (state.Route == RouteState.Probing)
                {
                    RefreshProbeMetadata(state);
                    if (!state.HandshakeObserved && !state.FallbackPending && now >= state.NativeDecisionAt)
                    {
                        state.FallbackPending = true;
                        state.FallbackInitiated = true;
                        state.NextFallbackAt = 0f;
                        CoopMod.Logger.LogInfo($"[RNET] Negotiating native fallback with {PeerName(state.Peer)}");
                    }
                    if (state.FallbackPending)
                    {
                        if (state.FallbackCommitPending && now >= state.FallbackCommitExpiresAt)
                        {
                            // The responder may have forgotten our challenge after prolonged
                            // loss. Return to REQUEST so both sides rebuild the fallback exchange.
                            state.FallbackCommitPending = false;
                            state.RemoteFallbackChallenge = 0;
                            state.NextFallbackAt = 0f;
                        }
                        if (state.FallbackResponsePending && !state.FallbackInitiated &&
                            !state.FallbackCommitPending && now >= state.FallbackResponseExpiresAt)
                        {
                            ClearFallback(state);
                            state.NativeDecisionAt = now + NativeDecisionDelay;
                            continue;
                        }
                        if (now >= state.NextFallbackAt)
                        {
                            state.NextFallbackAt = now + HelloInterval;
                            if (state.FallbackCommitPending)
                                SendControl(state, KindFallbackCommit);
                            else if (state.FallbackInitiated)
                                SendControl(state, KindFallbackRequest);
                        }
                        continue;
                    }
                    if (state.HasRemoteNonce && state.MetadataConfirmed && now >= state.NextHelloAt)
                    {
                        state.NextHelloAt = now + HelloInterval;
                        SendControl(state, KindHello);
                    }
                }
                else if (state.Route == RouteState.Native)
                {
                    FlushPendingNative(state);
                }
                else if (state.Route == RouteState.Channel)
                {
                    FlushPendingChannel(state, now);
                    TickChannel(state, now);
                }
            }
        }

        private void HandleEnvelope(CSteamID sender, byte[] data, int length)
        {
            if (length < EnvelopeHeaderBytes || data[1] != WireVersion)
                return;

            byte kind = data[2];
            int lane = data[3];
            ulong sourceNonce = BitConverter.ToUInt64(data, 4);
            ulong targetNonce = BitConverter.ToUInt64(data, 12);
            ulong session = BitConverter.ToUInt64(data, 20);
            if (sourceNonce == 0 || targetNonce != localNonce)
                return;
            if (kind == KindHello && length != HelloBytes)
                return;
            if (kind == KindHelloAck && length != HelloAckBytes)
                return;
            if (kind == KindCommit && length != CommitBytes)
                return;
            if ((kind == KindFallbackRequest || kind == KindFallbackCommit ||
                 kind == KindFallbackCommitAck) && length != FallbackBytes)
                return;
            if (kind == KindFallbackAck && length != FallbackAckBytes)
                return;
            if (kind == KindData && length < DataHeaderBytes)
                return;
            if (kind != KindHello && kind != KindHelloAck &&
                kind != KindCommit && kind != KindFallbackRequest &&
                kind != KindFallbackAck && kind != KindFallbackCommit &&
                kind != KindFallbackCommitAck && kind != KindData)
                return;
            if (kind == KindData && !IsSupportedLane(lane))
                return;

            PeerState state = GetOrCreatePeer(sender);

            if (!RemoteMetadataMatches(sender, sourceNonce))
            {
                // A frame targeting this fresh local nonce is v2 evidence. Prevent a native
                // timeout while Steam member data catches up, but do not trust its payload yet.
                if (state.Route == RouteState.Unknown)
                    state.Route = RouteState.Probing;
                state.HandshakeObserved = true;
                if (!state.HasRemoteNonce)
                {
                    state.RemoteNonce = sourceNonce;
                    state.HasRemoteNonce = true;
                }
                return;
            }

            state.MetadataConfirmed = true;
            if (!BindRemoteNonce(state, sourceNonce))
                return;
            if (kind == KindHello || kind == KindHelloAck || kind == KindCommit ||
                kind == KindFallbackRequest || kind == KindFallbackAck ||
                kind == KindFallbackCommit || kind == KindFallbackCommitAck)
            {
                if (lane != 0 || session != ComputeBaseSession(sender, sourceNonce, 0))
                    return;

                if (kind == KindFallbackRequest)
                {
                    ulong requestChallenge = BitConverter.ToUInt64(data, 28);
                    if (requestChallenge == 0 || state.Route == RouteState.Channel)
                        return;
                    state.FallbackPending = true;
                    state.FallbackResponsePending = true;
                    state.RemoteFallbackChallenge = requestChallenge;
                    state.FallbackResponseExpiresAt = Time.realtimeSinceStartup + FallbackResponderLifetime;
                    SendControl(state, KindFallbackAck);
                    return;
                }

                if (kind == KindFallbackAck)
                {
                    ulong echoedChallenge = BitConverter.ToUInt64(data, 28);
                    ulong sourceChallenge = BitConverter.ToUInt64(data, 36);
                    if (!state.FallbackInitiated || echoedChallenge != state.LocalChallenge ||
                        sourceChallenge == 0 ||
                        state.Route == RouteState.Channel)
                        return;
                    state.FallbackPending = true;
                    state.FallbackCommitPending = true;
                    state.RemoteFallbackChallenge = sourceChallenge;
                    state.FallbackCommitExpiresAt = Time.realtimeSinceStartup + FallbackCommitPhase;
                    state.NextFallbackAt = 0f;
                    SendControl(state, KindFallbackCommit);
                    return;
                }

                if (kind == KindFallbackCommit)
                {
                    ulong echoedChallenge = BitConverter.ToUInt64(data, 28);
                    if ((!state.FallbackResponsePending && state.Route != RouteState.Native) ||
                        echoedChallenge != state.LocalChallenge || state.Route == RouteState.Channel)
                        return;
                    SendControl(state, KindFallbackCommitAck);
                    LockNative(state, "native fallback committed by peer", force: true);
                    return;
                }

                if (kind == KindFallbackCommitAck)
                {
                    ulong commitAckChallenge = BitConverter.ToUInt64(data, 28);
                    if (!state.FallbackCommitPending || commitAckChallenge != state.LocalChallenge ||
                        state.Route == RouteState.Channel)
                        return;
                    LockNative(state, "native fallback commit acknowledged", force: true);
                    return;
                }

                // Normal channel controls never mix with fallback negotiation.
                if (state.Route == RouteState.Native || state.FallbackPending)
                    return;

                state.HandshakeObserved = true;
                if (kind == KindHello)
                {
                    ulong challenge = BitConverter.ToUInt64(data, 28);
                    if (challenge == 0)
                        return;
                    if (state.Route == RouteState.Channel)
                    {
                        if (state.HasRemoteChallenge && state.RemoteChallenge == challenge)
                            SendControl(state, KindHelloAck);
                        return;
                    }

                    state.RemoteChallenge = challenge;
                    state.HasRemoteChallenge = true;
                    state.Route = RouteState.Probing;
                    SendControl(state, KindHelloAck);
                    return;
                }

                if (kind == KindHelloAck)
                {
                    ulong echoedChallenge = BitConverter.ToUInt64(data, 28);
                    ulong sourceChallenge = BitConverter.ToUInt64(data, 36);
                    if (echoedChallenge != state.LocalChallenge || sourceChallenge == 0)
                        return;
                    if (state.HasRemoteChallenge && state.RemoteChallenge != sourceChallenge &&
                        state.Route == RouteState.Channel)
                        return;

                    state.RemoteChallenge = sourceChallenge;
                    state.HasRemoteChallenge = true;
                    SendControl(state, KindCommit);
                    CommitChannel(state, "challenge response received");
                    return;
                }

                ulong echoedCommitChallenge = BitConverter.ToUInt64(data, 28);
                if (echoedCommitChallenge != state.LocalChallenge || !state.HasRemoteChallenge)
                    return;
                if (state.Route == RouteState.Channel)
                    return;

                state.Route = RouteState.Probing;
                CommitChannel(state, "challenge commit received");
                return;
            }

            if (state.Route == RouteState.Native || state.FallbackPending)
                return;
            if (!state.HasRemoteChallenge || session != ComputeChannelSession(state, lane))
                return;
            if (state.Route == RouteState.Unknown || state.Route == RouteState.Probing)
                CommitChannel(state, "valid DATA received");
            if (state.Route != RouteState.Channel)
                return;

            uint sequence = BitConverter.ToUInt32(data, 28);
            bool more = data[32] != 0;
            ReliableLane channel = GetOrCreateLane(state, lane);
            channel?.HandleData(sequence, more, data, DataHeaderBytes, length - DataHeaderBytes,
                Time.realtimeSinceStartup);
        }

        private void HandleAck(CSteamID sender, byte[] data, int length)
        {
            if (length != AckBytes || data[1] != WireVersion)
                return;

            int lane = data[2];
            if (!IsSupportedLane(lane))
                return;
            ulong sourceNonce = BitConverter.ToUInt64(data, 3);
            ulong targetNonce = BitConverter.ToUInt64(data, 11);
            ulong session = BitConverter.ToUInt64(data, 19);
            uint ackThrough = BitConverter.ToUInt32(data, 27);
            if (sourceNonce == 0 || targetNonce != localNonce ||
                !RemoteMetadataMatches(sender, sourceNonce) ||
                session == 0)
            {
                return;
            }

            PeerState state = GetOrCreatePeer(sender);
            state.MetadataConfirmed = true;
            if (state.Route == RouteState.Native || !BindRemoteNonce(state, sourceNonce))
                return;
            if (!state.HasRemoteChallenge || session != ComputeChannelSession(state, lane))
                return;
            state.HandshakeObserved = true;
            if (state.Route == RouteState.Unknown || state.Route == RouteState.Probing)
                CommitChannel(state, "valid ACK received");

            if (state.Route == RouteState.Channel && state.Lanes.TryGetValue(lane, out ReliableLane channel))
                channel.HandleAck(ackThrough, Time.realtimeSinceStartup);
        }

        private PeerState GetOrCreatePeer(CSteamID peer)
        {
            if (peers.TryGetValue(peer.m_SteamID, out PeerState state))
                return state;

            float now = Time.realtimeSinceStartup;
            state = new PeerState
            {
                Peer = peer,
                Route = RouteState.Unknown,
                LocalChallenge = CreateNonce(),
                NativeDecisionAt = now + NativeDecisionDelay,
                NextHelloAt = now
            };
            peers[peer.m_SteamID] = state;
            return state;
        }

        private bool CanOwnPeer(CSteamID peer)
        {
            return lobbyID != CSteamID.Nil && localNonce != 0 && peer != CSteamID.Nil &&
                   peer != SteamUser.GetSteamID() && manager.IsCurrentLobbyMember(peer);
        }

        private void TryStartProbe(PeerState state, float now)
        {
            if (state.Route != RouteState.Unknown)
                return;

            if (!TryGetRemoteCapability(state.Peer, out ulong nonce))
                return;

            state.RemoteNonce = nonce;
            state.HasRemoteNonce = true;
            state.MetadataConfirmed = true;
            state.Route = RouteState.Probing;
            state.NativeDecisionAt = now + NativeDecisionDelay;
            state.NextHelloAt = now;
            CoopMod.Logger.LogInfo($"[RNET] Probing reliable transport with {PeerName(state.Peer)}");
        }

        private void RefreshProbeMetadata(PeerState state)
        {
            if (!TryGetRemoteCapability(state.Peer, out ulong nonce))
                return;

            if (!state.HasRemoteNonce)
            {
                state.RemoteNonce = nonce;
                state.HasRemoteNonce = true;
                state.MetadataConfirmed = true;
            }
            else if (state.RemoteNonce != nonce)
            {
                if (!state.MetadataConfirmed || state.Route == RouteState.Probing)
                {
                    state.RemoteNonce = nonce;
                    state.MetadataConfirmed = true;
                    state.HasRemoteChallenge = false;
                    state.HandshakeObserved = false;
                    ClearFallback(state);
                    state.NativeDecisionAt = Time.realtimeSinceStartup + NativeDecisionDelay;
                }
                else
                {
                    // Do not overwrite an established session. A changed nonce means the old
                    // peer lifecycle should have been removed before a fresh state is created.
                    CoopMod.Logger.LogWarning($"[RNET] Ignored nonce change for {PeerName(state.Peer)}");
                }
            }
            else
            {
                state.MetadataConfirmed = true;
            }
        }

        private bool TryGetRemoteCapability(CSteamID peer, out ulong nonce)
        {
            nonce = 0;
            if (lobbyID == CSteamID.Nil || !manager.IsCurrentLobbyMember(peer))
                return false;

            string capability = SteamMatchmaking.GetLobbyMemberData(lobbyID, peer, CapabilityKey);
            string nonceText = SteamMatchmaking.GetLobbyMemberData(lobbyID, peer, NonceKey);
            return string.Equals(capability, CapabilityValue, StringComparison.Ordinal) &&
                   ulong.TryParse(nonceText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out nonce) &&
                   nonce != 0;
        }

        private bool RemoteMetadataMatches(CSteamID peer, ulong nonce)
        {
            return TryGetRemoteCapability(peer, out ulong advertised) && advertised == nonce;
        }

        private static bool BindRemoteNonce(PeerState state, ulong nonce)
        {
            if (state.HasRemoteNonce)
                return state.RemoteNonce == nonce;
            state.RemoteNonce = nonce;
            state.HasRemoteNonce = true;
            return true;
        }

        private static void ClearFallback(PeerState state)
        {
            state.FallbackPending = false;
            state.FallbackInitiated = false;
            state.FallbackCommitPending = false;
            state.FallbackResponsePending = false;
            state.RemoteFallbackChallenge = 0;
            state.NextFallbackAt = 0f;
            state.FallbackResponseExpiresAt = 0f;
            state.FallbackCommitExpiresAt = 0f;
        }

        private void CommitChannel(PeerState state, string reason)
        {
            if (state.Route == RouteState.Native || state.Route == RouteState.Channel || !state.HasRemoteNonce)
                return;

            state.Route = RouteState.Channel;
            state.NextHeartbeatAt = 0f;
            state.DegradedWarned = false;
            CoopMod.Logger.LogInfo($"[RNET] Reliable channel committed with {PeerName(state.Peer)} ({reason})");
            FlushPendingChannel(state, Time.realtimeSinceStartup);
        }

        private void LockNative(PeerState state, string reason, bool force = false)
        {
            if (state.Route == RouteState.Channel || state.Route == RouteState.Native ||
                (!force && state.Route == RouteState.Probing && state.HandshakeObserved))
            {
                return;
            }

            state.Route = RouteState.Native;
            CoopMod.Logger.LogInfo($"[RNET] Using native reliable with {PeerName(state.Peer)} ({reason})");
            FlushPendingNative(state);
        }

        private bool EnqueuePending(PeerState state, byte[] data, int lane)
        {
            if (state.Pending.Count >= MaxPendingMessages ||
                state.PendingBytes > MaxPendingBytes - data.Length)
            {
                CoopMod.Logger.LogWarning($"[RNET] Pending route queue full for {PeerName(state.Peer)}");
                return false;
            }

            var copy = new byte[data.Length];
            Buffer.BlockCopy(data, 0, copy, 0, data.Length);
            state.Pending.Enqueue(new PendingMessage { Lane = lane, Data = copy });
            state.PendingBytes += copy.Length;
            return true;
        }

        private void FlushPendingNative(PeerState state)
        {
            while (state.Route == RouteState.Native && state.Pending.Count > 0)
            {
                PendingMessage next = state.Pending.Peek();
                if (!manager.SendNativeReliableFromTransport(state.Peer, next.Data, next.Lane))
                    return;
                state.Pending.Dequeue();
                state.PendingBytes -= next.Data.Length;
            }
        }

        private void FlushPendingChannel(PeerState state, float now)
        {
            while (state.Route == RouteState.Channel && state.Pending.Count > 0)
            {
                PendingMessage next = state.Pending.Peek();
                ReliableLane channel = GetOrCreateLane(state, next.Lane);
                if (channel == null || !channel.Send(next.Data, now))
                    return;
                state.Pending.Dequeue();
                state.PendingBytes -= next.Data.Length;
            }
        }

        private ReliableLane GetOrCreateLane(PeerState state, int lane)
        {
            if (!IsSupportedLane(lane))
                return null;
            if (state.Lanes.TryGetValue(lane, out ReliableLane channel))
                return channel;

            channel = new ReliableLane(
                lane,
                GetMaxMessageBytes(lane),
                wire => SendWire(state.Peer, wire),
                message => Deliver(state.Peer, message, lane),
                () => BuildAck(state, lane));
            channel.FinalizeDataWire = wire =>
            {
                WriteUInt64(wire, 4, localNonce);
                WriteUInt64(wire, 12, state.RemoteNonce);
                WriteUInt64(wire, 20, ComputeChannelSession(state, lane));
            };
            state.Lanes[lane] = channel;
            return channel;
        }

        private void TickChannel(PeerState state, float now)
        {
            foreach (ReliableLane channel in state.Lanes.Values)
                channel.Tick(now);

            if (now >= state.NextHeartbeatAt)
            {
                state.NextHeartbeatAt = now + HeartbeatInterval;
                ReliableLane control = GetOrCreateLane(state, 0);
                control?.Send(new[] { (byte)Op.Heartbeat }, now);
            }

            bool stalled = false;
            foreach (ReliableLane channel in state.Lanes.Values)
            {
                if (channel.HasPending && now - channel.LastAckProgressAt > DegradedWarningAfter)
                {
                    stalled = true;
                    break;
                }
            }

            float lastRaw = manager.GetLastPacketRealtimeFrom(state.Peer);
            bool peerFresh = lastRaw > 0f && now - lastRaw <= RawPeerFreshWindow;
            if (stalled && peerFresh && !state.DegradedWarned)
            {
                state.DegradedWarned = true;
                string name = PeerName(state.Peer);
                CoopMod.Logger.LogWarning($"[RNET] Reliable delivery to {name} is stalled; retained packets continue retransmitting");
                NetworkDiagnostics.RecordError("RNET", $"reliable delivery to {name} stalled");
            }
            else if (!stalled)
            {
                state.DegradedWarned = false;
            }
        }

        private void SendControl(PeerState state, byte kind)
        {
            if (!state.HasRemoteNonce)
                return;

            int length;
            if (kind == KindHello)
                length = HelloBytes;
            else if (kind == KindHelloAck)
                length = HelloAckBytes;
            else if (kind == KindCommit)
                length = CommitBytes;
            else if (kind == KindFallbackAck)
                length = FallbackAckBytes;
            else if (kind == KindFallbackRequest || kind == KindFallbackCommit ||
                     kind == KindFallbackCommitAck)
                length = FallbackBytes;
            else
                return;

            if ((kind == KindHelloAck || kind == KindCommit) && !state.HasRemoteChallenge)
                return;
            if (kind == KindFallbackAck && state.RemoteFallbackChallenge == 0)
                return;
            if ((kind == KindFallbackCommit || kind == KindFallbackCommitAck) &&
                state.RemoteFallbackChallenge == 0)
                return;

            var wire = new byte[length];
            wire[0] = TagEnvelope;
            wire[1] = WireVersion;
            wire[2] = kind;
            wire[3] = 0;
            WriteUInt64(wire, 4, localNonce);
            WriteUInt64(wire, 12, state.RemoteNonce);
            WriteUInt64(wire, 20, ComputeBaseSession(state.Peer, state.RemoteNonce, 0));
            if (kind == KindHello)
            {
                WriteUInt64(wire, 28, state.LocalChallenge);
            }
            else if (kind == KindHelloAck)
            {
                WriteUInt64(wire, 28, state.RemoteChallenge);
                WriteUInt64(wire, 36, state.LocalChallenge);
            }
            else if (kind == KindCommit)
            {
                WriteUInt64(wire, 28, state.RemoteChallenge);
            }
            else if (kind == KindFallbackRequest)
            {
                WriteUInt64(wire, 28, state.LocalChallenge);
            }
            else if (kind == KindFallbackAck)
            {
                WriteUInt64(wire, 28, state.RemoteFallbackChallenge);
                WriteUInt64(wire, 36, state.LocalChallenge);
            }
            else
            {
                WriteUInt64(wire, 28, state.RemoteFallbackChallenge);
            }
            SendWire(state.Peer, wire);
        }

        private byte[] BuildAck(PeerState state, int lane)
        {
            if (!state.HasRemoteNonce)
                return null;
            var wire = new byte[AckBytes];
            wire[0] = TagAck;
            wire[1] = WireVersion;
            wire[2] = (byte)lane;
            WriteUInt64(wire, 3, localNonce);
            WriteUInt64(wire, 11, state.RemoteNonce);
            WriteUInt64(wire, 19, ComputeChannelSession(state, lane));
            return wire;
        }

        private bool SendWire(CSteamID peer, byte[] wire)
        {
            if (wire == null || wire.Length == 0)
                return false;
            if (ChaosDropRate > 0f && ChaosRng.NextDouble() < ChaosDropRate)
                return true;
            return manager.SendRawDatagram(peer, wire);
        }

        private void Deliver(CSteamID peer, byte[] message, int lane)
        {
            if (lobbyID == CSteamID.Nil ||
                !peers.TryGetValue(peer.m_SteamID, out PeerState state) ||
                state.Route != RouteState.Channel ||
                !manager.IsCurrentLobbyMember(peer))
            {
                return;
            }

            try
            {
                manager.DispatchReassembledMessage(peer, message, lane);
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"[RNET] Reassembled message handler failed for {PeerName(peer)}: {ex}");
                NetworkDiagnostics.RecordError("RNET", ex.Message);
            }
        }

        private ulong ComputeBaseSession(CSteamID peer, ulong remoteNonce, int lane)
        {
            return ComputeSession(peer, remoteNonce, 0, 0, lane, includeChallenges: false);
        }

        private ulong ComputeChannelSession(PeerState state, int lane)
        {
            return ComputeSession(
                state.Peer,
                state.RemoteNonce,
                state.LocalChallenge,
                state.RemoteChallenge,
                lane,
                includeChallenges: true);
        }

        private ulong ComputeSession(CSteamID peer, ulong remoteNonce, ulong localChallenge,
            ulong remoteChallenge, int lane, bool includeChallenges)
        {
            ulong localID = SteamUser.GetSteamID().m_SteamID;
            ulong remoteID = peer.m_SteamID;
            ulong hash = 14695981039346656037UL;
            Hash(ref hash, WireVersion);
            Hash(ref hash, lobbyID.m_SteamID);
            if (localID < remoteID)
            {
                Hash(ref hash, localID);
                Hash(ref hash, localNonce);
                if (includeChallenges)
                    Hash(ref hash, localChallenge);
                Hash(ref hash, remoteID);
                Hash(ref hash, remoteNonce);
                if (includeChallenges)
                    Hash(ref hash, remoteChallenge);
            }
            else
            {
                Hash(ref hash, remoteID);
                Hash(ref hash, remoteNonce);
                if (includeChallenges)
                    Hash(ref hash, remoteChallenge);
                Hash(ref hash, localID);
                Hash(ref hash, localNonce);
                if (includeChallenges)
                    Hash(ref hash, localChallenge);
            }
            Hash(ref hash, (ulong)(byte)lane);
            return hash == 0 ? 1UL : hash;
        }

        private static void Hash(ref ulong hash, ulong value)
        {
            unchecked
            {
                for (int i = 0; i < 8; i++)
                {
                    hash ^= (byte)(value >> (i * 8));
                    hash *= 1099511628211UL;
                }
            }
        }

        private static ulong CreateNonce()
        {
            var bytes = new byte[8];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                do
                {
                    rng.GetBytes(bytes);
                } while (BitConverter.ToUInt64(bytes, 0) == 0);
            }
            return BitConverter.ToUInt64(bytes, 0);
        }

        private static void WriteUInt64(byte[] destination, int offset, ulong value)
        {
            destination[offset] = (byte)value;
            destination[offset + 1] = (byte)(value >> 8);
            destination[offset + 2] = (byte)(value >> 16);
            destination[offset + 3] = (byte)(value >> 24);
            destination[offset + 4] = (byte)(value >> 32);
            destination[offset + 5] = (byte)(value >> 40);
            destination[offset + 6] = (byte)(value >> 48);
            destination[offset + 7] = (byte)(value >> 56);
        }

        private static void WriteUInt32(byte[] destination, int offset, uint value)
        {
            destination[offset] = (byte)value;
            destination[offset + 1] = (byte)(value >> 8);
            destination[offset + 2] = (byte)(value >> 16);
            destination[offset + 3] = (byte)(value >> 24);
        }

        private static string PeerName(CSteamID peer)
        {
            string name = SteamFriends.GetFriendPersonaName(peer);
            return string.IsNullOrEmpty(name) ? peer.ToString() : name;
        }

        private static bool IsSupportedLane(int lane)
        {
            return lane == 0 || lane == 1;
        }

        private static int GetMaxMessageBytes(int lane)
        {
            return lane == 1 ? MaxLobbyLaneMessageBytes : MaxLogicalMessageBytes;
        }

        private sealed class ReliableLane
        {
            private const int SendWindow = 96;
            private const int MaxBufferedFragments = 16384;
            private const int MaxBufferedBytes = 16 * 1024 * 1024;
            private const int MaxReorderFragments = 512;
            private const int MaxReorderBytes = 2 * 1024 * 1024;
            private const uint MaxSequenceDistance = 4096;
            private const float AckDelay = 0.03f;
            private const float SendFailureRetry = 0.05f;
            private const float MinRto = 0.2f;
            private const float MaxRto = 3f;
            // Steam sends are synchronous enough that releasing or retrying the whole
            // 96-fragment window in one Update can consume an entire frame.
            private const int MaxDataSendAttemptsPerFrame = 16;

            private sealed class OutFragment
            {
                public uint Sequence;
                public byte[] Wire;
                public float FirstSentAt;
                public float NextAttemptAt;
                public int SuccessfulAttempts;
            }

            private sealed class InFragment
            {
                public bool More;
                public byte[] Payload;
            }

            private readonly int lane;
            private readonly int maxLogicalMessageBytes;
            private readonly Func<byte[], bool> rawSend;
            private readonly Action<byte[]> deliver;
            private readonly Func<byte[]> ackFactory;
            private readonly Queue<OutFragment> sendQueue = new Queue<OutFragment>();
            private readonly SortedDictionary<uint, OutFragment> unacked = new SortedDictionary<uint, OutFragment>();
            private readonly Dictionary<uint, InFragment> reorder = new Dictionary<uint, InFragment>();
            private readonly List<byte> assembly = new List<byte>();
            private readonly List<byte[]> completedMessages = new List<byte[]>();
            private readonly List<uint> acknowledgedSequences = new List<uint>();
            private uint sendSequence;
            private uint receiveNext;
            private int bufferedSendBytes;
            private int reorderBytes;
            private bool discardingOversizedMessage;
            private bool ackDue;
            private float ackAt;
            private bool hasHighestSent;
            private uint highestSent;
            private float smoothedRtt;
            private float rttVariance;
            private float rto = 0.35f;
            private int sendAttemptFrame = -1;
            private int sendAttemptsThisFrame;

            public bool HasPending => sendQueue.Count > 0 || unacked.Count > 0;
            public float LastAckProgressAt { get; private set; }

            public ReliableLane(int lane, int maxLogicalMessageBytes, Func<byte[], bool> rawSend,
                Action<byte[]> deliver, Func<byte[]> ackFactory)
            {
                this.lane = lane;
                this.maxLogicalMessageBytes = maxLogicalMessageBytes;
                this.rawSend = rawSend;
                this.deliver = deliver;
                this.ackFactory = ackFactory;
                LastAckProgressAt = Time.realtimeSinceStartup;
            }

            public bool Send(byte[] payload, float now)
            {
                if (payload == null || payload.Length == 0 || payload.Length > maxLogicalMessageBytes)
                    return false;

                int fragmentCount = (payload.Length + FragmentPayloadBytes - 1) / FragmentPayloadBytes;
                int wireBytes = payload.Length + fragmentCount * DataHeaderBytes;
                if (fragmentCount > MaxBufferedFragments ||
                    sendQueue.Count + unacked.Count > MaxBufferedFragments - fragmentCount ||
                    bufferedSendBytes > MaxBufferedBytes - wireBytes ||
                    sendSequence > uint.MaxValue - (uint)fragmentCount)
                {
                    return false;
                }

                int offset = 0;
                while (offset < payload.Length)
                {
                    int count = Math.Min(FragmentPayloadBytes, payload.Length - offset);
                    bool more = offset + count < payload.Length;
                    uint sequence = sendSequence++;
                    var wire = new byte[DataHeaderBytes + count];
                    wire[0] = TagEnvelope;
                    wire[1] = WireVersion;
                    wire[2] = KindData;
                    wire[3] = (byte)lane;
                    // Nonce/session fields are filled by the outer transport before first send.
                    // The delegate below receives the finalized frame through FinalizeDataWire.
                    WriteUInt32(wire, 28, sequence);
                    wire[32] = more ? (byte)1 : (byte)0;
                    Buffer.BlockCopy(payload, offset, wire, DataHeaderBytes, count);
                    sendQueue.Enqueue(new OutFragment { Sequence = sequence, Wire = wire, NextAttemptAt = now });
                    bufferedSendBytes += wire.Length;
                    offset += count;
                }

                Pump(now);
                return true;
            }

            public void HandleData(uint sequence, bool more, byte[] wire, int payloadOffset,
                int payloadLength, float now)
            {
                if (payloadLength < 0 || payloadLength > FragmentPayloadBytes)
                    return;

                completedMessages.Clear();
                if (sequence < receiveNext)
                {
                    ScheduleAck(now);
                }
                else if (sequence == receiveNext)
                {
                    Consume(sequence, more, wire, payloadOffset, payloadLength, completedMessages, now);
                    while (reorder.TryGetValue(receiveNext, out InFragment next))
                    {
                        reorder.Remove(receiveNext);
                        reorderBytes -= next.Payload.Length;
                        ConsumeBuffered(next, completedMessages, now);
                    }
                }
                else
                {
                    uint distance = sequence - receiveNext;
                    if (distance <= MaxSequenceDistance &&
                        reorder.Count < MaxReorderFragments &&
                        !reorder.ContainsKey(sequence) &&
                        reorderBytes <= MaxReorderBytes - payloadLength)
                    {
                        var copy = new byte[payloadLength];
                        Buffer.BlockCopy(wire, payloadOffset, copy, 0, payloadLength);
                        reorder[sequence] = new InFragment { More = more, Payload = copy };
                        reorderBytes += copy.Length;
                    }
                    ScheduleAck(now);
                }

                for (int i = 0; i < completedMessages.Count; i++)
                {
                    try
                    {
                        deliver(completedMessages[i]);
                    }
                    catch (Exception ex)
                    {
                        CoopMod.Logger.LogError($"[RNET] Lane {lane} delivery callback failed: {ex}");
                    }
                }
                completedMessages.Clear();
            }

            public void HandleAck(uint ackThrough, float now)
            {
                if (!hasHighestSent || ackThrough > highestSent)
                    return;

                acknowledgedSequences.Clear();
                foreach (KeyValuePair<uint, OutFragment> pair in unacked)
                {
                    if (pair.Key > ackThrough)
                        break;
                    // A cumulative ACK cannot legitimately cover a fragment that this
                    // process has never successfully handed to Steam.
                    if (pair.Value.SuccessfulAttempts == 0)
                        return;
                    acknowledgedSequences.Add(pair.Key);
                }

                if (acknowledgedSequences.Count == 0)
                    return;

                for (int i = 0; i < acknowledgedSequences.Count; i++)
                {
                    uint sequence = acknowledgedSequences[i];
                    OutFragment fragment = unacked[sequence];
                    if (fragment.SuccessfulAttempts == 1 && fragment.FirstSentAt > 0f)
                        UpdateRto(now - fragment.FirstSentAt);
                    bufferedSendBytes -= fragment.Wire.Length;
                    unacked.Remove(sequence);
                }
                LastAckProgressAt = now;
                Pump(now);
            }

            public void Tick(float now)
            {
                Pump(now);
                foreach (OutFragment fragment in unacked.Values)
                {
                    if (now >= fragment.NextAttemptAt)
                        TrySend(fragment, now);
                }

                if (ackDue && now >= ackAt && receiveNext > 0)
                    TrySendAck(now);
            }

            private void Pump(float now)
            {
                if (unacked.Count == 0 && sendQueue.Count > 0)
                    LastAckProgressAt = now;

                while (sendQueue.Count > 0 && unacked.Count < SendWindow)
                {
                    OutFragment fragment = sendQueue.Dequeue();
                    unacked[fragment.Sequence] = fragment;
                    TrySend(fragment, now);
                }
            }

            private void TrySend(OutFragment fragment, float now)
            {
                int frame = Time.frameCount;
                if (sendAttemptFrame != frame)
                {
                    sendAttemptFrame = frame;
                    sendAttemptsThisFrame = 0;
                }
                if (sendAttemptsThisFrame >= MaxDataSendAttemptsPerFrame)
                    return;
                sendAttemptsThisFrame++;

                FinalizeDataWire?.Invoke(fragment.Wire);

                bool sent = rawSend(fragment.Wire);
                if (!sent)
                {
                    fragment.NextAttemptAt = now + SendFailureRetry;
                    return;
                }

                if (fragment.SuccessfulAttempts == 0)
                    fragment.FirstSentAt = now;
                fragment.SuccessfulAttempts++;
                hasHighestSent = true;
                if (fragment.Sequence > highestSent)
                    highestSent = fragment.Sequence;
                int exponent = Math.Min(3, Math.Max(0, fragment.SuccessfulAttempts - 1));
                fragment.NextAttemptAt = now + Math.Min(MaxRto, rto * (1 << exponent));
            }

            private void Consume(uint sequence, bool more, byte[] wire, int payloadOffset,
                int payloadLength, List<byte[]> completed, float now)
            {
                var payload = new byte[payloadLength];
                Buffer.BlockCopy(wire, payloadOffset, payload, 0, payloadLength);
                receiveNext = sequence + 1;
                ApplyFragment(more, payload, completed);
                ScheduleAck(now);
            }

            private void ConsumeBuffered(InFragment fragment, List<byte[]> completed, float now)
            {
                receiveNext++;
                ApplyFragment(fragment.More, fragment.Payload, completed);
                ScheduleAck(now);
            }

            private void ApplyFragment(bool more, byte[] payload, List<byte[]> completed)
            {
                if (discardingOversizedMessage)
                {
                    if (!more)
                        discardingOversizedMessage = false;
                    return;
                }

                if (assembly.Count > maxLogicalMessageBytes - payload.Length)
                {
                    assembly.Clear();
                    discardingOversizedMessage = more;
                    return;
                }

                assembly.AddRange(payload);
                if (!more)
                {
                    if (assembly.Count > 0)
                        completed.Add(assembly.ToArray());
                    assembly.Clear();
                }
            }

            private void ScheduleAck(float now)
            {
                if (!ackDue)
                    ackAt = now + AckDelay;
                ackDue = true;
            }

            private void TrySendAck(float now)
            {
                byte[] ack = ackFactory();
                if (ack == null)
                    return;
                WriteUInt32(ack, 27, receiveNext - 1);
                if (rawSend(ack))
                {
                    ackDue = false;
                }
                else
                {
                    ackAt = now + SendFailureRetry;
                }
            }

            private void UpdateRto(float sample)
            {
                sample = Math.Max(0.001f, sample);
                if (smoothedRtt == 0f)
                {
                    smoothedRtt = sample;
                    rttVariance = sample * 0.5f;
                }
                else
                {
                    rttVariance = 0.75f * rttVariance + 0.25f * Math.Abs(smoothedRtt - sample);
                    smoothedRtt = 0.875f * smoothedRtt + 0.125f * sample;
                }
                rto = Math.Max(MinRto, Math.Min(MaxRto, smoothedRtt + 4f * rttVariance));
            }

            public Action<byte[]> FinalizeDataWire { private get; set; }
        }
    }
}

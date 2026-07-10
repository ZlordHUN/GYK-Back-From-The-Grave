using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace GraveyardKeeperCoop.Network
{
    /// <summary>
    /// Application-level reliability layer over Steam UNRELIABLE P2P datagrams.
    ///
    /// WHY: on this game's Steamworks build (SDK 1.42 / Steamworks.NET in Assembly-CSharp-firstpass)
    /// Steam's own k_EP2PSendReliable can silently drop every packet client->host mid-session on
    /// strict-NAT/CGNAT connections. Measured on a live session: 43 reliable heartbeats sent, 0 received,
    /// while unreliable datagrams flowed both ways the whole time and P2PSessionConnectFail never fired.
    /// SendP2PPacket returning true only means "handed to Steam", not "delivered". The modern relay-backed
    /// SteamNetworkingMessages API is absent from this SDK, so we build TCP-lite over the one primitive
    /// that demonstrably works on such links: unreliable datagrams. In-order, ACKed, retransmitted,
    /// fragmented, and self-healing (dead-link detector + automatic stream resync).
    ///
    /// Ported from the Graveyard Keeper Multiplayer mod (github.com/Zonda001/graveyard-keeper-multiplayer,
    /// MIT) where this design survived six live-test rounds plus chaos testing with a packet-loss injector.
    ///
    /// WIRE FORMAT (every frame is sent UNRELIABLE, channel 0):
    ///   DATA: [0xFE][epoch:1][seq:uint32][more:1][payload...]   more=1 -> same message continues
    ///   ACK : [0xFF][epoch:1][ackThrough:uint32]                cumulative: everything &lt;= ackThrough delivered
    /// 0xFE/0xFF are permanently reserved and must never become Op values (see BinaryProtocol.cs).
    /// epoch = stream generation, bumped by the auto-resync. Frames from another epoch are dropped on
    /// arrival: without this, stale in-flight fragments from before a reset could enter the reorder buffer
    /// and later be delivered instead of the true fragment with the same seq (silent stream corruption).
    /// </summary>
    public sealed class ReliableChannel
    {
        private const byte TAG_DATA = 0xFE;
        private const byte TAG_ACK = 0xFF;
        private const int FRAG_PAYLOAD = 1100;   // inner bytes per datagram (+7 header stays under Steam's ~1200 unreliable cap)
        private const float RESEND_AFTER = 0.30f; // retransmit an unacked fragment after this many seconds
        private const float ACK_COALESCE = 0.03f; // batch the cumulative ack instead of one per fragment
        private const int MAX_REORDER = 8192;     // safety cap on the out-of-order buffer
        private const int SEND_WINDOW = 96;       // max fragments in flight (~106KB). A multi-MB payload
                                                  // (save transfer) burst-sent all at once overflows Steam's
                                                  // per-connection send buffer: SendP2PPacket starts returning
                                                  // false for EVERYTHING including other systems' packets and
                                                  // our own ACKs never come back - the detector then reads the
                                                  // self-inflicted silence as a dead link and a resync wipes
                                                  // the payload. Fragments beyond the window wait in sendQueue
                                                  // and follow as ACKs free slots.

        public readonly CSteamID Peer;

        /// <summary>Send one raw datagram to Peer, k_EP2PSendUnreliable. Injected by ReliableTransport.</summary>
        public Action<byte[]> RawSend;
        /// <summary>Hand a fully reassembled inner message to the packet dispatcher. Injected by ReliableTransport.</summary>
        public Action<byte[]> OnDeliver;

        // Send side: our outgoing reliable stream
        private sealed class Pending { public uint Seq; public byte[] Wire; public float FirstSent; public float LastSent; public int Tries; }
        private uint sendSeq;
        private readonly SortedDictionary<uint, Pending> unacked = new SortedDictionary<uint, Pending>();
        private readonly Queue<Pending> sendQueue = new Queue<Pending>(); // built but not yet on the wire

        // Outbound-death signals (read by the transport's degraded-link detector). A side whose outbound
        // died still receives the peer just fine, so inbound silence never fires HERE - only the OTHER
        // machine would notice. These two signals catch the local half of the breakage.
        private float notReadyDropSince; // first Send() swallowed by !ready (0 = none)
        public bool OutboundLooksDead { get; private set; }
        public string OutboundDeathReason { get; private set; }

        // Recv side: the peer's incoming reliable stream
        private uint recvNext;                                                        // next in-order seq to deliver
        private readonly Dictionary<uint, byte[]> reorder = new Dictionary<uint, byte[]>(); // buffered future fragments
        private readonly List<byte> assembly = new List<byte>();                      // accumulates the current message
        private bool ackDue;
        private float ackTimer;
        private bool ready;
        private byte epoch;

        /// <summary>Timestamp of the last fully delivered inner message (feeds the inbound-silence detector).</summary>
        public float LastDeliveryTime { get; private set; }
        public byte Epoch => epoch;

        public ReliableChannel(CSteamID peer)
        {
            Peer = peer;
            Reset(0);
        }

        /// <summary>Realign both stream directions to seq 0 for the given generation and unblock sending.</summary>
        public void Reset(byte newEpoch)
        {
            sendSeq = 0; recvNext = 0;
            unacked.Clear(); sendQueue.Clear(); reorder.Clear(); assembly.Clear();
            ackDue = false; ackTimer = 0f;
            ready = true;
            epoch = newEpoch;
            notReadyDropSince = 0f; OutboundLooksDead = false; OutboundDeathReason = null;
        }

        /// <summary>
        /// Resync initiator half 1: align to the PENDING epoch and go quiet. Receiving stays fully
        /// functional (HandleIncoming ignores ready), so the peer's post-Reset seq-0 stream is accepted
        /// and acked immediately; our own sends stay blocked until Resume() - sending seq 0 before the
        /// peer resets would desync the fresh streams.
        /// </summary>
        public void PrepareResync(byte pendingEpoch)
        {
            sendSeq = 0; recvNext = 0;
            unacked.Clear(); sendQueue.Clear(); reorder.Clear(); assembly.Clear();
            ackDue = false; ackTimer = 0f;
            ready = false;
            epoch = pendingEpoch;
            notReadyDropSince = 0f;
        }

        /// <summary>
        /// Resync initiator half 2: the peer confirmed. ONLY unblock sending - a full Reset here would
        /// rewind recvNext past anything the peer already delivered since ITS reset and redeliver duplicates.
        /// </summary>
        public void Resume()
        {
            ready = true;
            notReadyDropSince = 0f; OutboundLooksDead = false; OutboundDeathReason = null;
        }

        /// <summary>Queue a logical message: fragment it, store each fragment for retransmit, fire all now.</summary>
        public void Send(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return;
            if (!ready)
            {
                // Swallowing sends while resync is pending; timestamp the first one so Tick can flag
                // the outbound as dead if it goes on (Reset/Resume clears it).
                if (notReadyDropSince == 0f) notReadyDropSince = Time.time;
                return;
            }

            int off = 0;
            do
            {
                int chunk = Math.Min(FRAG_PAYLOAD, payload.Length - off);
                bool more = (off + chunk) < payload.Length;
                uint seq = sendSeq++;
                var wire = new byte[7 + chunk];
                wire[0] = TAG_DATA;
                wire[1] = epoch;
                BitConverter.GetBytes(seq).CopyTo(wire, 2);
                wire[6] = (byte)(more ? 1 : 0);
                Buffer.BlockCopy(payload, off, wire, 7, chunk);
                sendQueue.Enqueue(new Pending { Seq = seq, Wire = wire });
                off += chunk;
            } while (off < payload.Length);
            PumpSendQueue(Time.time);
        }

        // Move queued fragments onto the wire while the in-flight window has room. Queued wires
        // always carry the current epoch: Reset/PrepareResync clear the queue along with unacked.
        private void PumpSendQueue(float now)
        {
            while (sendQueue.Count > 0 && unacked.Count < SEND_WINDOW)
            {
                var p = sendQueue.Dequeue();
                p.FirstSent = now; p.LastSent = now; p.Tries = 1;
                unacked[p.Seq] = p;
                WireSend(p.Wire);
            }
        }

        /// <summary>A 0xFE/0xFF datagram arrived. Delivers reassembled messages via OnDeliver, acks via RawSend.</summary>
        public void HandleIncoming(byte[] data, int length)
        {
            if (data == null || length < 1) return;

            if (data[0] == TAG_ACK)
            {
                if (length < 6) return;
                if (data[1] != epoch) return; // stale generation - ignore
                uint ackThrough = BitConverter.ToUInt32(data, 2);
                // Drop every fragment the peer confirmed. unacked is sorted, so stop at the first unconfirmed.
                var done = new List<uint>();
                foreach (var kv in unacked) { if (kv.Key <= ackThrough) done.Add(kv.Key); else break; }
                for (int i = 0; i < done.Count; i++) unacked.Remove(done[i]);
                if (done.Count > 0) PumpSendQueue(Time.time); // freed window slots - let queued fragments follow
                return;
            }

            if (data[0] == TAG_DATA)
            {
                if (length < 7) return;
                if (data[1] != epoch) return; // stale generation - never let it into the reorder buffer
                uint seq = BitConverter.ToUInt32(data, 2);

                if (seq < recvNext) { ackDue = true; return; } // duplicate - re-ack so the sender drops it
                if (seq == recvNext)
                {
                    DeliverFragment(data, length);
                    recvNext++;
                    while (reorder.TryGetValue(recvNext, out var buf)) // drain the now-contiguous run
                    {
                        reorder.Remove(recvNext);
                        DeliverFragment(buf, buf.Length);
                        recvNext++;
                    }
                }
                else if (reorder.Count < MAX_REORDER && !reorder.ContainsKey(seq))
                {
                    // Future fragment - buffer until the gap fills. Copy: the caller may reuse its buffer.
                    var copy = new byte[length];
                    Buffer.BlockCopy(data, 0, copy, 0, length);
                    reorder[seq] = copy;
                }
                ackDue = true;
            }
        }

        // Strip the 7-byte header, append to the assembly buffer; on more=0 emit the complete inner message.
        private void DeliverFragment(byte[] wire, int length)
        {
            for (int i = 7; i < length; i++) assembly.Add(wire[i]);
            if (wire[6] == 0) // last fragment of this message
            {
                var msg = assembly.ToArray();
                assembly.Clear();
                if (msg.Length > 0)
                {
                    LastDeliveryTime = Time.time;
                    OnDeliver?.Invoke(msg);
                }
            }
        }

        /// <summary>Called every frame: retransmit timed-out fragments + flush a coalesced cumulative ack.</summary>
        public void Tick(float dt)
        {
            float now = Time.time;
            PumpSendQueue(now); // covers the Resume() case: queue drained even with no fresh ACK or Send
            float oldestUnacked = 0f; // unacked is sorted by seq -> the first entry is the oldest fragment
            foreach (var kv in unacked)
            {
                var p = kv.Value;
                if (oldestUnacked == 0f) oldestUnacked = now - p.FirstSent;
                if (now - p.LastSent >= RESEND_AFTER)
                {
                    WireSend(p.Wire);
                    p.LastSent = now;
                    p.Tries++;
                }
            }

            // Outbound health for the degraded-link detector: dead if sends have been swallowed for a
            // while (resync never completed) or the oldest fragment went unacked through ~40 retransmits.
            if (notReadyDropSince != 0f && now - notReadyDropSince > 12f)
            { OutboundLooksDead = true; OutboundDeathReason = "sends blocked - resync never completed"; }
            else if (oldestUnacked > 12f)
            { OutboundLooksDead = true; OutboundDeathReason = $"no ACKs for {oldestUnacked:F0}s"; }
            else if (OutboundLooksDead && notReadyDropSince == 0f && oldestUnacked < 2f)
            { OutboundLooksDead = false; OutboundDeathReason = null; } // acks resumed - recovered

            if (ackDue && recvNext > 0) // recvNext==0 -> nothing delivered yet, an ack would be meaningless
            {
                ackTimer += dt;
                if (ackTimer >= ACK_COALESCE)
                {
                    ackTimer = 0f; ackDue = false;
                    var ack = new byte[6];
                    ack[0] = TAG_ACK;
                    ack[1] = epoch;
                    BitConverter.GetBytes(recvNext - 1).CopyTo(ack, 2);
                    WireSend(ack);
                }
            }
        }

        // Every outgoing datagram funnels through here so the chaos injector (simulated packet loss for
        // testing, see ReliableTransport.ChaosDropRate) can swallow a fraction of them.
        private void WireSend(byte[] wire)
        {
            if (ReliableTransport.ChaosDropRate > 0f &&
                ReliableTransport.ChaosRng.NextDouble() < ReliableTransport.ChaosDropRate)
                return; // "lost" - never hits the wire
            RawSend?.Invoke(wire);
        }
    }

    /// <summary>
    /// Per-peer manager for <see cref="ReliableChannel"/>: routes reliable game traffic over the
    /// unreliable-datagram channels, keeps them alive with heartbeats, detects dead links (both
    /// directions) and heals them with an automatic stream resync - no rejoin needed.
    ///
    /// Peers advertise support via the "rnet" lobby member data key. Peers without it (older mod
    /// versions) keep using native Steam reliable exactly as before, so mixed lobbies stay compatible.
    /// </summary>
    public sealed class ReliableTransport
    {
        /// <summary>Lobby member data key advertising this transport. Value = feature version.</summary>
        public const string CapabilityKey = "rnet";
        public const string CapabilityValue = "1";

        private const float HEARTBEAT_EVERY = 2f;     // reliable-lane keepalive; feeds the peer's inbound-silence eye
        private const float PEER_FRESH_WINDOW = 5f;   // any packet from the peer within this = peer is alive
        private const float REL_SILENCE_WARN = 15f;   // reliable deliveries missing this long (peer alive) = inbound dead
        private const float RESYNC_REQUEST_EVERY = 1f;
        private const float RESYNC_WARN_AFTER = 20f;  // log an error if a resync episode runs this long with no recovery

        // Chaos injector (testing only): probability that an outgoing channel datagram is dropped,
        // simulating a lossy uplink. Shared across channels; wired to a config entry by the plugin.
        public static float ChaosDropRate;
        internal static readonly System.Random ChaosRng = new System.Random();

        /// <summary>Fired after a resync completes for a peer. Sync systems that keep authoritative
        /// world state can subscribe to re-broadcast it: anything unacked during the dead window is
        /// gone by design and only comes back via a state re-send.</summary>
        public event Action<CSteamID> OnChannelResynced;

        private sealed class PeerState
        {
            public ReliableChannel Channel;
            public bool CapabilityConfirmed;   // saw "rnet" member data or an incoming channel frame
            public float NextHeartbeatAt;
            public float EligibleSince;        // detector grace anchor: channel creation / last epoch change
            // Resync driver
            public bool ResyncPending;
            public byte ResyncPendingEpoch;
            public float NextResyncRequestAt;
            public int ResyncAttempts;         // per-episode; reset on recovery
            public float EpisodeStartedAt;
            public bool WarnedThisEpisode;
        }

        private readonly Dictionary<ulong, PeerState> peers = new Dictionary<ulong, PeerState>();
        private readonly SteamP2PManager manager;

        public ReliableTransport(SteamP2PManager manager)
        {
            this.manager = manager;
        }

        #region Capability / lifecycle

        /// <summary>True if reliable traffic to this peer should ride the channel instead of native Steam reliable.</summary>
        public bool PeerSupportsChannel(CSteamID peer)
        {
            var state = GetOrCreateState(peer);
            if (state.CapabilityConfirmed) return true;

            // An already-open channel is proof of capability by itself: inbound channels are only
            // created for incoming 0xFE/0xFF frames, and only a channel-speaking build emits those.
            // This closes the join race where the peer's first frames (e.g. the save list request)
            // arrive before their lobby member data has propagated through the Steam backend -
            // without this, the host's reply and the save transfer fall back to native reliable.
            if (state.Channel != null)
            {
                state.CapabilityConfirmed = true;
                CoopMod.Logger.LogInfo($"[RNET] {SteamFriends.GetFriendPersonaName(peer)} supports the reliable channel (confirmed by incoming channel traffic)");
                return true;
            }

            var lobbyID = SteamLobbyManager.Instance?.CurrentLobbyID ?? CSteamID.Nil;
            if (lobbyID == CSteamID.Nil) return false;

            string cap = SteamMatchmaking.GetLobbyMemberData(lobbyID, peer, CapabilityKey);
            if (!string.IsNullOrEmpty(cap))
            {
                state.CapabilityConfirmed = true;
                CoopMod.Logger.LogInfo($"[RNET] {SteamFriends.GetFriendPersonaName(peer)} supports the reliable channel (v{cap})");
                // Open the channel right away so heartbeats start flowing in BOTH directions even
                // before this side has any reliable payload to send - otherwise the peer's detector
                // would see a live raw lane with a silent reliable lane and fire a needless resync.
                GetOrCreateChannel(peer);
                return true;
            }
            return false;
        }

        private PeerState GetOrCreateState(CSteamID peer)
        {
            if (peers.TryGetValue(peer.m_SteamID, out var state)) return state;
            state = new PeerState();
            peers[peer.m_SteamID] = state;
            return state;
        }

        private ReliableChannel GetOrCreateChannel(CSteamID peer)
        {
            var state = GetOrCreateState(peer);
            if (state.Channel == null)
            {
                var channel = new ReliableChannel(peer);
                channel.RawSend = wire => manager.SendRawDatagram(peer, wire);
                channel.OnDeliver = msg => manager.DispatchReassembledMessage(peer, msg);
                state.Channel = channel;
                state.EligibleSince = Time.time;
                state.NextHeartbeatAt = 0f;
                CoopMod.Logger.LogInfo($"[RNET] Reliable channel opened for {SteamFriends.GetFriendPersonaName(peer)}");
            }
            return state.Channel;
        }

        /// <summary>Tear down the channel for a peer that left the lobby.</summary>
        public void RemovePeer(CSteamID peer)
        {
            if (peers.Remove(peer.m_SteamID))
                CoopMod.Logger.LogInfo($"[RNET] Reliable channel closed for {SteamFriends.GetFriendPersonaName(peer)}");
        }

        /// <summary>Tear down all channels (we left the lobby / session ended).</summary>
        public void Clear()
        {
            peers.Clear();
        }

        #endregion

        #region Send / receive

        /// <summary>Carry a reliable message to a channel-capable peer. Always accepts (retransmits internally).</summary>
        public bool Send(CSteamID peer, byte[] data)
        {
            GetOrCreateChannel(peer).Send(data);
            return true;
        }

        /// <summary>
        /// Inspect a raw incoming packet. Returns true if it was a channel frame (0xFE/0xFF) and was
        /// consumed; false lets the caller dispatch it normally. An incoming frame is also proof the
        /// peer runs this transport, so the capability flag is set without waiting for member data.
        /// </summary>
        public bool HandleRawIncoming(CSteamID sender, byte[] data, int length)
        {
            if (length < 1) return false;
            byte tag = data[0];
            if (tag != 0xFE && tag != 0xFF) return false;

            // Frames can trail in after a session ends: the peer's channel keeps retransmitting
            // until it notices the lobby is gone, and an unguarded frame would re-create a ghost
            // channel here (capability confirmed, fresh sequence state) that survives Clear() and
            // pollutes the next session. Only current lobby members may open a channel - and the
            // gate keys on having an open channel, not a peer entry, because our own outbound path
            // can leave a channel-less entry behind after Clear(). Consuming the frame is safe for
            // a legitimate joiner too, because channel frames are retransmitted until acked.
            bool hasChannel = peers.TryGetValue(sender.m_SteamID, out var existing) && existing.Channel != null;
            if (!hasChannel && !IsLobbyMember(sender))
                return true;

            var state = GetOrCreateState(sender);
            state.CapabilityConfirmed = true;
            GetOrCreateChannel(sender).HandleIncoming(data, length);
            return true;
        }

        private static bool IsLobbyMember(CSteamID peer)
        {
            var lobbyID = SteamLobbyManager.Instance?.CurrentLobbyID ?? CSteamID.Nil;
            if (lobbyID == CSteamID.Nil) return false;

            int memberCount = SteamMatchmaking.GetNumLobbyMembers(lobbyID);
            for (int i = 0; i < memberCount; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(lobbyID, i) == peer)
                    return true;
            }
            return false;
        }

        #endregion

        #region Tick: heartbeat + degraded-link detector + resync driver

        /// <summary>Call once per frame from SteamP2PManager.Update.</summary>
        public void Tick()
        {
            if (peers.Count == 0) return;

            float now = Time.time;
            float dt = Time.deltaTime;

            foreach (var kv in peers)
            {
                var state = kv.Value;
                var channel = state.Channel;
                if (channel == null) continue;

                channel.Tick(dt);

                // Keepalive on the reliable lane. Even with zero gameplay traffic the peer keeps
                // receiving deliveries, so its inbound-silence eye stays quiet. LOAD-BEARING: without
                // it, an idle-but-healthy link is indistinguishable from a dead one and the detector
                // would fire spurious resyncs.
                if (now >= state.NextHeartbeatAt)
                {
                    state.NextHeartbeatAt = now + HEARTBEAT_EVERY;
                    channel.Send(new[] { (byte)Op.Heartbeat });
                }

                TickDetector(channel.Peer, state, channel, now);
            }
        }

        private void TickDetector(CSteamID peer, PeerState state, ReliableChannel channel, float now)
        {
            // Resync in flight: keep asking until the peer answers (raw unreliable, loss beaten by repeats).
            if (state.ResyncPending)
            {
                if (now >= state.NextResyncRequestAt)
                {
                    state.NextResyncRequestAt = now + RESYNC_REQUEST_EVERY;
                    state.ResyncAttempts++;
                    SendControl(peer, Op.ResyncRequest, state.ResyncPendingEpoch);
                }
                if (!state.WarnedThisEpisode && state.ResyncAttempts >= 2 && now - state.EpisodeStartedAt > RESYNC_WARN_AFTER)
                {
                    state.WarnedThisEpisode = true;
                    string who = SteamFriends.GetFriendPersonaName(peer);
                    CoopMod.Logger.LogError($"[RNET] Link to {who} degraded and auto-resync is not getting through " +
                                            $"({state.ResyncAttempts} attempts). If this persists, that player should rejoin.");
                    NetworkDiagnostics.RecordError("RNET", $"resync to {who} not completing");
                }
                return;
            }

            // Eligibility: the peer must be demonstrably alive on the raw lane while the reliable lane
            // is silent. The grace anchor moves on every epoch change - right after a resync the streams
            // are empty and silence is expected until the first heartbeat lands (lesson from live test:
            // without this grace the detector re-fires instantly and resyncs loop forever).
            float lastAlive = manager.GetLastPacketTimeFrom(peer);
            if (lastAlive <= 0f || now - lastAlive > PEER_FRESH_WINDOW) return; // peer gone/idle - not our case

            float reliableSilence = now - Mathf.Max(channel.LastDeliveryTime, state.EligibleSince);
            bool inboundDead = reliableSilence > REL_SILENCE_WARN;
            bool outboundDead = channel.OutboundLooksDead;
            if (!inboundDead && !outboundDead)
            {
                state.ResyncAttempts = 0; // healthy - close the episode
                state.WarnedThisEpisode = false;
                return;
            }

            // Degraded: start a resync episode as the initiator.
            byte nextEpoch = (byte)(channel.Epoch + 1);
            string peerName = SteamFriends.GetFriendPersonaName(peer);
            CoopMod.Logger.LogWarning($"[RNET] Link to {peerName} degraded " +
                                      $"(inbound silent {reliableSilence:F0}s{(outboundDead ? $"; outbound: {channel.OutboundDeathReason}" : "")}) " +
                                      $"- starting auto-resync to epoch {nextEpoch}");
            channel.PrepareResync(nextEpoch);
            state.ResyncPending = true;
            state.ResyncPendingEpoch = nextEpoch;
            state.NextResyncRequestAt = 0f;
            state.EpisodeStartedAt = now;
            state.EligibleSince = now; // epoch changed - restart the silence grace window
        }

        #endregion

        #region Resync control messages (arrive as raw unreliable Op messages, outside the channel)

        /// <summary>Peer asks us to realign our shared streams to a new epoch.</summary>
        public void OnResyncRequest(CSteamID sender, byte requestedEpoch)
        {
            var state = GetOrCreateState(sender);
            state.CapabilityConfirmed = true; // resync ops only come from transport-capable peers
            var channel = GetOrCreateChannel(sender);

            if (state.ResyncPending && state.ResyncPendingEpoch == requestedEpoch)
            {
                // Both sides detected the breakage and initiated the same epoch. PrepareResync already
                // cleared our streams - a full Reset here could rewind recvNext under traffic the peer
                // already sent post-reset (UDP reorder) and redeliver duplicates. Just resume.
                CompleteResync(sender, state, channel, "both sides initiated");
            }
            else if (channel.Epoch == requestedEpoch)
            {
                // Duplicate request for an epoch we already applied - our confirm was lost, resend it.
                SendControl(sender, Op.ResyncConfirm, requestedEpoch, times: 3);
            }
            else
            {
                CoopMod.Logger.LogInfo($"[RNET] Resync requested by {SteamFriends.GetFriendPersonaName(sender)} - realigning to epoch {requestedEpoch}");
                channel.Reset(requestedEpoch);
                state.EligibleSince = Time.time; // epoch changed - restart the silence grace window
                // If we were mid-resync ourselves on a DIFFERENT epoch (possible when one side
                // recreated the channel after a lobby flap), the peer's epoch supersedes ours -
                // keeping our stale request alive would reset the peer right back and ping-pong.
                state.ResyncPending = false;
                state.ResyncAttempts = 0;
                state.WarnedThisEpisode = false;
                SendControl(sender, Op.ResyncConfirm, requestedEpoch, times: 3);
                OnChannelResynced?.Invoke(sender);
            }
        }

        /// <summary>Peer confirmed the epoch we asked for - our initiator half can resume sending.</summary>
        public void OnResyncConfirm(CSteamID sender, byte confirmedEpoch)
        {
            if (!peers.TryGetValue(sender.m_SteamID, out var state) || state.Channel == null) return;
            if (!state.ResyncPending || state.ResyncPendingEpoch != confirmedEpoch) return;
            CompleteResync(sender, state, state.Channel, "peer confirmed");
        }

        private void CompleteResync(CSteamID peer, PeerState state, ReliableChannel channel, string how)
        {
            channel.Resume();
            state.ResyncPending = false;
            state.ResyncAttempts = 0;
            state.WarnedThisEpisode = false;
            state.EligibleSince = Time.time;
            CoopMod.Logger.LogInfo($"[RNET] Link to {SteamFriends.GetFriendPersonaName(peer)} recovered ({how}, epoch {channel.Epoch})");
            OnChannelResynced?.Invoke(peer);
        }

        // Control messages ride RAW unreliable: native reliable is exactly what we cannot trust
        // mid-session, and the channel itself is what is being repaired. Loss is beaten by repetition.
        private void SendControl(CSteamID peer, Op op, byte epoch, int times = 1)
        {
            var msg = new[] { (byte)op, epoch };
            for (int i = 0; i < times; i++)
                manager.SendRawDatagram(peer, msg);
        }

        #endregion
    }
}

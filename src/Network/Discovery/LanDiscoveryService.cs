using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Steamworks;

namespace GraveyardKeeperCoop.Network
{
    public sealed class LanDiscoveryService : IDisposable
    {
        private const int DiscoveryPort = 34245;
        private const string ProtocolMagic = "GYKCOOP_LAN_V1";
        private const string DiscoveryRequest = "DISCOVER";
        private const int AdvertiseIntervalMs = 1500;
        private const int ReceiveTimeoutMs = 250;
        private const int ServerStaleMs = 6500;

        private static readonly LanDiscoveryService _instance = new LanDiscoveryService();
        public static LanDiscoveryService Instance => _instance;

        private readonly object advertisedLock = new object();
        private readonly object serversLock = new object();
        private readonly object listenerStateLock = new object();
        private readonly object listenSendLock = new object();
        private readonly Dictionary<ulong, LanServerInfo> discoveredServers = new Dictionary<ulong, LanServerInfo>();

        private LanServerInfo advertisedSession;
        private bool browserListenRequested;
        private bool advertiseListenRequested;
        private volatile bool advertising;
        private volatile bool listening;
        private Thread advertiseThread;
        private Thread listenThread;
        private UdpClient advertiseClient;
        private UdpClient listenClient;

        private LanDiscoveryService()
        {
        }

        public void SetAdvertisedSession(LanServerInfo session)
        {
            lock (advertisedLock)
            {
                advertisedSession = session?.Clone();
            }
        }

        public void StartAdvertising()
        {
            if (!advertising)
            {
                advertising = true;
                advertiseThread = new Thread(AdvertiseLoop)
                {
                    IsBackground = true,
                    Name = "GYK-LAN-Advertise"
                };
                advertiseThread.Start();
                CoopMod.Logger.LogInfo($"[LAN] Discovery advertising started on UDP {DiscoveryPort}");
            }

            SetAdvertiseListeningRequested(true);
        }

        public void StopAdvertising()
        {
            SetAdvertiseListeningRequested(false);

            lock (advertisedLock)
            {
                advertisedSession = null;
            }

            if (advertising)
            {
                advertising = false;
                try { advertiseClient?.Close(); } catch { }
                advertiseClient = null;
                TryJoinThread(advertiseThread);
                advertiseThread = null;
                CoopMod.Logger.LogInfo("[LAN] Discovery advertising stopped");
            }
        }

        public void StartListening()
        {
            lock (listenerStateLock)
            {
                browserListenRequested = true;
                EnsureListenerStartedLocked();
            }
        }

        public void StopListening()
        {
            lock (listenerStateLock)
            {
                browserListenRequested = false;
                StopListenerIfUnusedLocked();
            }

            lock (serversLock)
            {
                discoveredServers.Clear();
            }
        }

        public void SendDiscoveryProbe()
        {
            byte[] payload = Encoding.UTF8.GetBytes(
                $"{ProtocolMagic}|{DiscoveryRequest}|{PluginInfo.PLUGIN_VERSION ?? ""}|{CoopProtocol.Revision}");
            int sent = SendToBroadcastAddresses(payload, "Discovery probe");
            if (sent > 0)
                CoopMod.Logger.LogDebug($"[LAN] Discovery probe sent to {sent} broadcast address(es)");
        }

        public List<LanServerInfo> GetServers()
        {
            lock (serversLock)
            {
                PruneStaleServersLocked();
                var servers = new List<LanServerInfo>(discoveredServers.Count);
                foreach (var server in discoveredServers.Values)
                {
                    servers.Add(server.Clone());
                }

                return servers;
            }
        }

        public void Dispose()
        {
            StopAdvertising();
            StopListening();
        }

        private void SetAdvertiseListeningRequested(bool requested)
        {
            lock (listenerStateLock)
            {
                advertiseListenRequested = requested;
                if (requested)
                    EnsureListenerStartedLocked();
                else
                    StopListenerIfUnusedLocked();
            }
        }

        private void EnsureListenerStartedLocked()
        {
            if (listening)
                return;

            try
            {
                listenClient = CreateListener();
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[LAN] Listener failed: {ex.Message}");
                listenClient = null;
                return;
            }

            listening = true;
            listenThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "GYK-LAN-Listen"
            };
            listenThread.Start();
            CoopMod.Logger.LogInfo($"[LAN] Discovery listener started on UDP {DiscoveryPort}");
        }

        private void StopListenerIfUnusedLocked()
        {
            if (browserListenRequested || advertiseListenRequested || !listening)
                return;

            listening = false;
            try { listenClient?.Close(); } catch { }
            listenClient = null;
            TryJoinThread(listenThread);
            listenThread = null;
            CoopMod.Logger.LogInfo("[LAN] Discovery listener stopped");
        }

        private void AdvertiseLoop()
        {
            try
            {
                advertiseClient = new UdpClient();
                advertiseClient.EnableBroadcast = true;

                while (advertising)
                {
                    LanServerInfo snapshot;
                    lock (advertisedLock)
                    {
                        snapshot = advertisedSession?.Clone();
                    }

                    if (snapshot != null && snapshot.LobbyID != CSteamID.Nil)
                    {
                        byte[] payload = Encoding.UTF8.GetBytes(Encode(snapshot));
                        SendToBroadcastAddresses(advertiseClient, payload, "Broadcast send");
                    }

                    Thread.Sleep(AdvertiseIntervalMs);
                }
            }
            catch (Exception ex)
            {
                if (advertising)
                    CoopMod.Logger.LogWarning($"[LAN] Advertise loop stopped unexpectedly: {ex.Message}");
            }
            finally
            {
                try { advertiseClient?.Close(); } catch { }
                advertiseClient = null;
            }
        }

        private void ListenLoop()
        {
            try
            {
                while (listening)
                {
                    try
                    {
                        IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                        byte[] bytes = listenClient.Receive(ref remote);
                        if (IsDiscoveryRequest(bytes))
                        {
                            RespondToDiscoveryRequest(remote);
                            continue;
                        }

                        if (TryDecode(bytes, remote.Address, out LanServerInfo info))
                        {
                            bool isNewServer;
                            lock (serversLock)
                            {
                                isNewServer = !discoveredServers.ContainsKey(info.LobbyID.m_SteamID);
                                discoveredServers[info.LobbyID.m_SteamID] = info;
                                PruneStaleServersLocked();
                            }

                            if (isNewServer)
                            {
                                string hostName = string.IsNullOrEmpty(info.HostName) ? info.Endpoint : info.HostName;
                                CoopMod.Logger.LogInfo($"[LAN] Discovered LAN lobby: {hostName}'s Game at {info.Endpoint}");
                            }
                        }
                    }
                    catch (SocketException ex)
                    {
                        if (ex.SocketErrorCode != SocketError.TimedOut && listening)
                            CoopMod.Logger.LogWarning($"[LAN] Discovery receive error: {ex.SocketErrorCode}");
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (listening)
                            CoopMod.Logger.LogWarning($"[LAN] Discovery packet ignored: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                if (listening)
                    CoopMod.Logger.LogWarning($"[LAN] Listener failed: {ex.Message}");
            }
            finally
            {
                try { listenClient?.Close(); } catch { }
                listenClient = null;
            }
        }

        private void RespondToDiscoveryRequest(IPEndPoint remote)
        {
            if (!advertising || remote == null)
                return;

            LanServerInfo snapshot;
            lock (advertisedLock)
            {
                snapshot = advertisedSession?.Clone();
            }

            if (snapshot == null || snapshot.LobbyID == CSteamID.Nil)
                return;

            UdpClient client = listenClient;
            if (client == null)
                return;

            byte[] payload = Encoding.UTF8.GetBytes(Encode(snapshot));
            var responseTarget = new IPEndPoint(remote.Address, DiscoveryPort);
            try
            {
                lock (listenSendLock)
                {
                    client.Send(payload, payload.Length, responseTarget);
                }
                CoopMod.Logger.LogDebug($"[LAN] Answered discovery probe from {remote.Address}");
            }
            catch (Exception ex)
            {
                if (advertising)
                    CoopMod.Logger.LogWarning($"[LAN] Discovery probe response failed to {responseTarget}: {ex.Message}");
            }
        }

        private static UdpClient CreateListener()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.ExclusiveAddressUse = false;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            socket.ReceiveTimeout = ReceiveTimeoutMs;
            return new UdpClient { Client = socket };
        }

        private static IEnumerable<IPAddress> GetBroadcastAddresses()
        {
            var addresses = new List<IPAddress> { IPAddress.Broadcast };

            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                        continue;

                    IPInterfaceProperties properties = nic.GetIPProperties();
                    foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null)
                            continue;

                        byte[] ipBytes = unicast.Address.GetAddressBytes();
                        byte[] maskBytes = unicast.IPv4Mask.GetAddressBytes();
                        uint ip = BitConverter.ToUInt32(ipBytes, 0);
                        uint mask = BitConverter.ToUInt32(maskBytes, 0);
                        uint broadcast = ip | ~mask;
                        var address = new IPAddress(BitConverter.GetBytes(broadcast));
                        if (!addresses.Contains(address))
                            addresses.Add(address);
                    }
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogWarning($"[LAN] Failed to enumerate broadcast addresses: {ex.Message}");
            }

            return addresses;
        }

        private int SendToBroadcastAddresses(byte[] payload, string operation)
        {
            using (var client = new UdpClient())
            {
                client.EnableBroadcast = true;
                return SendToBroadcastAddresses(client, payload, operation);
            }
        }

        private static int SendToBroadcastAddresses(UdpClient client, byte[] payload, string operation)
        {
            int sent = 0;
            foreach (IPAddress broadcastAddress in GetBroadcastAddresses())
            {
                try
                {
                    client.Send(payload, payload.Length, new IPEndPoint(broadcastAddress, DiscoveryPort));
                    sent++;
                }
                catch (Exception ex)
                {
                    CoopMod.Logger.LogWarning($"[LAN] {operation} failed to {broadcastAddress}: {ex.Message}");
                }
            }

            return sent;
        }

        private static string Encode(LanServerInfo info)
        {
            return string.Join("|", new[]
            {
                ProtocolMagic,
                PluginInfo.PLUGIN_VERSION ?? "",
                info.LobbyID.m_SteamID.ToString(),
                info.HostSteamID.m_SteamID.ToString(),
                EncodeText(info.HostName),
                info.CurrentPlayers.ToString(),
                info.MaxPlayers.ToString(),
                EncodeText(info.Status),
                EncodeText(info.Visibility),
                EncodeText(info.ModVersion),
                EncodeText(info.DLCRequirements),
                EncodeText(info.ProtocolRevision)
            });
        }

        private static bool IsDiscoveryRequest(byte[] bytes)
        {
            string message = Encoding.UTF8.GetString(bytes);
            string[] parts = message.Split('|');
            return parts.Length >= 2 && parts[0] == ProtocolMagic && parts[1] == DiscoveryRequest;
        }

        private static bool TryDecode(byte[] bytes, IPAddress remoteAddress, out LanServerInfo info)
        {
            info = null;
            string message = Encoding.UTF8.GetString(bytes);
            string[] parts = message.Split('|');
            if (parts.Length < 10 || parts[0] != ProtocolMagic)
                return false;

            if (!ulong.TryParse(parts[2], out ulong lobbyValue) ||
                !ulong.TryParse(parts[3], out ulong hostValue) ||
                !int.TryParse(parts[5], out int currentPlayers) ||
                !int.TryParse(parts[6], out int maxPlayers))
            {
                return false;
            }

            var lobbyID = new CSteamID(lobbyValue);
            if (lobbyID == CSteamID.Nil)
                return false;

            info = new LanServerInfo
            {
                LobbyID = lobbyID,
                HostSteamID = new CSteamID(hostValue),
                HostName = DecodeText(parts[4]),
                CurrentPlayers = currentPlayers,
                MaxPlayers = maxPlayers,
                Status = DecodeText(parts[7]),
                Visibility = DecodeText(parts[8]),
                ModVersion = DecodeText(parts[9]),
                DLCRequirements = parts.Length > 10 ? DecodeText(parts[10]) : "",
                ProtocolRevision = parts.Length > 11 ? DecodeText(parts[11]) : "",
                Endpoint = remoteAddress?.ToString() ?? "",
                LastSeenUtcTicks = DateTime.UtcNow.Ticks
            };
            return true;
        }

        private static string EncodeText(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static string DecodeText(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch
            {
                return "";
            }
        }

        private void PruneStaleServersLocked()
        {
            long now = DateTime.UtcNow.Ticks;
            List<ulong> stale = null;
            foreach (var pair in discoveredServers)
            {
                double ageMs = TimeSpan.FromTicks(now - pair.Value.LastSeenUtcTicks).TotalMilliseconds;
                if (ageMs > ServerStaleMs)
                {
                    if (stale == null)
                        stale = new List<ulong>();
                    stale.Add(pair.Key);
                }
            }

            if (stale == null)
                return;

            foreach (ulong lobbyID in stale)
            {
                discoveredServers.Remove(lobbyID);
            }
        }

        private static void TryJoinThread(Thread thread)
        {
            if (thread == null)
                return;

            try
            {
                if (thread.IsAlive)
                    thread.Join(500);
            }
            catch { }
        }
    }

    public sealed class LanServerInfo
    {
        public CSteamID LobbyID;
        public CSteamID HostSteamID;
        public string HostName;
        public int CurrentPlayers;
        public int MaxPlayers;
        public string Status;
        public string Visibility;
        public string ModVersion;
        public string DLCRequirements;
        public string ProtocolRevision;
        public string Endpoint;
        public long LastSeenUtcTicks;

        public LanServerInfo Clone()
        {
            return (LanServerInfo)MemberwiseClone();
        }
    }
}

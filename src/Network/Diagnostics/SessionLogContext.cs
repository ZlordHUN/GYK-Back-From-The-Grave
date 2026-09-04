using System;
using System.Collections.Generic;
using System.Text;
using Steamworks;

namespace GraveyardKeeperCoop.Network
{
    /// <summary>
    /// Gives every multiplayer test run one host-authored session ID and stable
    /// P1-P4 aliases. The values live in Steam lobby metadata so every peer logs
    /// the same identifiers even when clocks or Steam display names differ.
    /// </summary>
    public static class SessionLogContext
    {
        public const string LobbyDataSessionId = "test_session_id";
        public const string LobbyDataFixtureId = "test_fixture_id";

        private const string PeerAliasPrefix = "test_peer_";
        private const string UnassignedFixture = "manual";

        private static readonly Dictionary<ulong, string> PeerAliases =
            new Dictionary<ulong, string>();
        private static readonly HashSet<string> LoggedPeerStates =
            new HashSet<string>(StringComparer.Ordinal);

        private static readonly string BootId =
            DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" +
            Guid.NewGuid().ToString("N").Substring(0, 6);

        private static CSteamID currentLobby = CSteamID.Nil;
        private static string currentSessionId;
        private static string currentFixtureId = UnassignedFixture;
        private static bool currentHost;
        private static int nextPeerNumber = 2;
        private static bool processLogged;
        private static bool sessionBeginLogged;

        public static string CurrentSessionId => currentSessionId ?? string.Empty;
        public static string CurrentFixtureId => currentFixtureId;

        public static void InitializeProcess()
        {
            if (processLogged) return;
            processLogged = true;

            ulong steamId = 0UL;
            string playerName = "unavailable";
            if (SteamManager.Initialized)
            {
                steamId = SteamUser.GetSteamID().m_SteamID;
                playerName = SteamFriends.GetPersonaName();
            }

            CoopMod.Logger.LogInfo(
                "[SESSION] event=process_start" +
                " boot=" + Field(BootId) +
                " version=" + Field(PluginInfo.PLUGIN_VERSION) +
                " protocol=" + Field(CoopProtocol.Revision) +
                " steam_id=" + steamId +
                " player=" + Field(playerName));
        }

        public static void BeginHostLobby(CSteamID lobby)
        {
            if (lobby == CSteamID.Nil || !SteamManager.Initialized) return;
            if (currentLobby == lobby && currentHost && !string.IsNullOrEmpty(currentSessionId))
            {
                RefreshFromLobby(lobby);
                return;
            }

            ResetState();
            currentLobby = lobby;
            currentHost = true;
            currentSessionId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" +
                               Guid.NewGuid().ToString("N").Substring(0, 8);
            currentFixtureId = NormalizeFixture(
                ModConfig.TestFixtureId != null ? ModConfig.TestFixtureId.Value : null);

            CSteamID local = SteamUser.GetSteamID();
            PeerAliases[local.m_SteamID] = "P1";
            SteamMatchmaking.SetLobbyData(lobby, LobbyDataSessionId, currentSessionId);
            SteamMatchmaking.SetLobbyData(lobby, LobbyDataFixtureId, currentFixtureId);
            SteamMatchmaking.SetLobbyData(lobby, AliasKey(local), "P1");

            LogSession("begin", local, "host");
            sessionBeginLogged = true;
            RefreshFromLobby(lobby);
        }

        public static void BeginJoinedLobby(CSteamID lobby, CSteamID owner)
        {
            if (lobby == CSteamID.Nil || !SteamManager.Initialized) return;
            if (currentLobby == lobby &&
                currentHost &&
                owner == SteamUser.GetSteamID() &&
                !string.IsNullOrEmpty(currentSessionId))
            {
                RefreshFromLobby(lobby);
                return;
            }
            if (currentLobby != lobby)
            {
                ResetState();
                currentLobby = lobby;
            }

            currentHost = owner == SteamUser.GetSteamID();
            ReadSharedMetadata(lobby);
            if (string.IsNullOrEmpty(currentSessionId))
            {
                CoopMod.Logger.LogWarning(
                    "[SESSION] Host session metadata was not available; " +
                    "waiting for the lobby-data update before logging the session");
            }
            else if (!sessionBeginLogged)
            {
                LogSession(
                    "begin",
                    SteamUser.GetSteamID(),
                    currentHost ? "host" : "client");
                sessionBeginLogged = true;
            }
            RefreshFromLobby(lobby);
        }

        public static void PeerJoined(CSteamID peer)
        {
            if (peer == CSteamID.Nil || currentLobby == CSteamID.Nil) return;

            if (currentHost && !PeerAliases.ContainsKey(peer.m_SteamID))
            {
                string existing = ReadAlias(currentLobby, peer);
                string alias = !string.IsNullOrEmpty(existing)
                    ? existing
                    : "P" + nextPeerNumber++;
                PeerAliases[peer.m_SteamID] = alias;
                SteamMatchmaking.SetLobbyData(currentLobby, AliasKey(peer), alias);
            }

            RefreshFromLobby(currentLobby);
            LogPeer("joined", peer);
        }

        public static void PeerLeft(CSteamID peer, string reason)
        {
            if (peer == CSteamID.Nil || currentLobby == CSteamID.Nil) return;
            LogPeer("left", peer, reason);
        }

        public static void RefreshFromLobby(CSteamID lobby)
        {
            if (lobby == CSteamID.Nil || !SteamManager.Initialized) return;
            if (currentLobby == CSteamID.Nil) currentLobby = lobby;
            if (currentLobby != lobby) return;

            ReadSharedMetadata(lobby);
            if (string.IsNullOrEmpty(currentSessionId)) return;
            if (!sessionBeginLogged)
            {
                LogSession(
                    "begin",
                    SteamUser.GetSteamID(),
                    currentHost ? "host" : "client");
                sessionBeginLogged = true;
            }
            int memberCount = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < memberCount; i++)
            {
                CSteamID member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
                string alias = ReadAlias(lobby, member);
                if (!string.IsNullOrEmpty(alias))
                    PeerAliases[member.m_SteamID] = alias;
                LogPeer("present", member);
            }
        }

        public static void EndLobby(string reason)
        {
            if (currentLobby == CSteamID.Nil) return;
            CSteamID local = SteamManager.Initialized
                ? SteamUser.GetSteamID()
                : CSteamID.Nil;
            LogSession("end", local, currentHost ? "host" : "client", reason);
            ResetState();
        }

        private static void ReadSharedMetadata(CSteamID lobby)
        {
            string session = SteamMatchmaking.GetLobbyData(lobby, LobbyDataSessionId);
            if (!string.IsNullOrWhiteSpace(session))
                currentSessionId = session.Trim();

            string fixture = SteamMatchmaking.GetLobbyData(lobby, LobbyDataFixtureId);
            if (!string.IsNullOrWhiteSpace(fixture))
                currentFixtureId = NormalizeFixture(fixture);
        }

        private static string ReadAlias(CSteamID lobby, CSteamID peer)
        {
            string alias = SteamMatchmaking.GetLobbyData(lobby, AliasKey(peer));
            return string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        }

        private static string AliasKey(CSteamID peer)
        {
            return PeerAliasPrefix + peer.m_SteamID;
        }

        private static void LogSession(
            string eventName,
            CSteamID ownerOrLocal,
            string role,
            string reason = null)
        {
            var line = new StringBuilder();
            line.Append("[SESSION] event=").Append(Field(eventName));
            line.Append(" session=").Append(Field(currentSessionId));
            line.Append(" fixture=").Append(Field(currentFixtureId));
            line.Append(" lobby=").Append(currentLobby.m_SteamID);
            line.Append(" role=").Append(Field(role));
            line.Append(" steam_id=").Append(ownerOrLocal.m_SteamID);
            line.Append(" boot=").Append(Field(BootId));
            line.Append(" version=").Append(Field(PluginInfo.PLUGIN_VERSION));
            line.Append(" protocol=").Append(Field(CoopProtocol.Revision));
            if (!string.IsNullOrEmpty(reason))
                line.Append(" reason=").Append(Field(reason));
            CoopMod.Logger.LogInfo(line.ToString());
        }

        private static void LogPeer(string eventName, CSteamID peer, string reason = null)
        {
            if (peer == CSteamID.Nil) return;
            string alias;
            if (!PeerAliases.TryGetValue(peer.m_SteamID, out alias))
                alias = "pending";

            CSteamID owner = SteamMatchmaking.GetLobbyOwner(currentLobby);
            string role = peer == owner ? "host" : "client";
            string signature = eventName + "|" + peer.m_SteamID + "|" + alias + "|" + reason;
            if (eventName == "present" && !LoggedPeerStates.Add(signature)) return;

            var line = new StringBuilder();
            line.Append("[PEER] event=").Append(Field(eventName));
            line.Append(" session=").Append(Field(currentSessionId));
            line.Append(" fixture=").Append(Field(currentFixtureId));
            line.Append(" alias=").Append(Field(alias));
            line.Append(" role=").Append(Field(role));
            line.Append(" steam_id=").Append(peer.m_SteamID);
            line.Append(" player=").Append(Field(SteamFriends.GetFriendPersonaName(peer)));
            if (!string.IsNullOrEmpty(reason))
                line.Append(" reason=").Append(Field(reason));
            CoopMod.Logger.LogInfo(line.ToString());
        }

        private static string NormalizeFixture(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return UnassignedFixture;
            string trimmed = value.Trim();
            var result = new StringBuilder(Math.Min(trimmed.Length, 64));
            for (int i = 0; i < trimmed.Length && result.Length < 64; i++)
            {
                char c = trimmed[i];
                if ((c >= 'a' && c <= 'z') ||
                    (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') ||
                    c == '-' || c == '_' || c == '.')
                {
                    result.Append(c);
                }
                else if (result.Length == 0 || result[result.Length - 1] != '-')
                {
                    result.Append('-');
                }
            }
            return result.Length == 0 ? UnassignedFixture : result.ToString();
        }

        private static string Field(string value)
        {
            if (string.IsNullOrEmpty(value)) return "none";
            var result = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                result.Append(char.IsWhiteSpace(c) || c == '=' ? '_' : c);
            }
            return result.ToString();
        }

        private static void ResetState()
        {
            currentLobby = CSteamID.Nil;
            currentSessionId = null;
            currentFixtureId = UnassignedFixture;
            currentHost = false;
            nextPeerNumber = 2;
            sessionBeginLogged = false;
            PeerAliases.Clear();
            LoggedPeerStates.Clear();
        }
    }
}

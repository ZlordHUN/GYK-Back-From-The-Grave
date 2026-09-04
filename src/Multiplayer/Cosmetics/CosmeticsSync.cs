using System;
using System.Collections.Generic;
using UnityEngine;
using Steamworks;
using GraveyardKeeperCoop.Network;

namespace GraveyardKeeperCoop.Multiplayer
{
    /// <summary>
    /// Manages player cosmetics synchronization between players.
    /// Stores local player cosmetics and applies remote player cosmetics.
    /// </summary>
    public class CosmeticsSync : SyncBehaviour
    {
        public const int SavedPresetSlotCount = 6;

        public static CosmeticsSync Instance => GetInstance<CosmeticsSync>();

        private PlayerCosmetics _localCosmetics;
        private PlayerCosmetics _remoteCosmetics;
        private PlayerCosmeticsDriver _localDriver;
        private PlayerCosmeticsDriver _remoteDriver;
        private readonly Dictionary<ulong, PlayerCosmetics> remoteCosmeticsByPeer =
            new Dictionary<ulong, PlayerCosmetics>();
        private readonly Dictionary<ulong, PlayerCosmeticsDriver> remoteDriversByPeer =
            new Dictionary<ulong, PlayerCosmeticsDriver>();
        private readonly HashSet<ulong> loadingExpectedPeers = new HashSet<ulong>();
        private readonly List<int>[] recentColors =
        {
            new List<int>(),
            new List<int>(),
            new List<int>(),
            new List<int>(),
            new List<int>(),
        };
        private readonly PlayerCosmetics[] savedPresetSlots =
            new PlayerCosmetics[SavedPresetSlotCount];
        private float _nextLocalDriverCheck;
        private float nextLoadingSynchronizationRetryAt;
        private bool loadingSynchronizationActive;

        private const float LoadingSynchronizationRetrySeconds = 1f;

        public PlayerCosmetics LocalCosmetics => _localCosmetics;
        public PlayerCosmetics RemoteCosmetics => _remoteCosmetics;

        protected override string LogPrefix => "[CosmeticsSync]";

        protected override void SubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnCosmeticsReceived -= OnCosmeticsReceived;
                SteamP2PManager.Instance.OnCosmeticsReceived += OnCosmeticsReceived;
                SteamP2PManager.Instance.OnCosmeticsRequested -= OnCosmeticsRequested;
                SteamP2PManager.Instance.OnCosmeticsRequested += OnCosmeticsRequested;
            }
        }

        protected override void UnsubscribeEvents()
        {
            if (SteamP2PManager.Instance != null)
            {
                SteamP2PManager.Instance.OnCosmeticsReceived -= OnCosmeticsReceived;
                SteamP2PManager.Instance.OnCosmeticsRequested -= OnCosmeticsRequested;
            }
        }

        protected override void OnAwake()
        {
            LoadLocalCosmetics();
        }

        private void LoadLocalCosmetics()
        {
            var config = ModConfig.Instance;
            if (config != null)
            {
                _localCosmetics = new PlayerCosmetics
                {
                    PantsColor = config.PlayerPantsColor.Value,
                    ShirtColor = config.PlayerShirtColor.Value,
                    HairColor = config.PlayerHairColor.Value,
                    SkinTone = config.PlayerSkinTone.Value,
                    EyeColor = config.PlayerEyeColor.Value,
                    PantsTone = config.PlayerPantsTone.Value,
                    ShirtTone = config.PlayerShirtTone.Value,
                    HairTone = config.PlayerHairTone.Value,
                    SkinShade = config.PlayerSkinShade.Value,
                    EyeTone = config.PlayerEyeTone.Value,
                };
                CoopMod.Logger.LogInfo($"{LogPrefix} Loaded cosmetics from config: {_localCosmetics}");
            }
            else
            {
                if (SteamManager.Initialized)
                {
                    ulong steamId = SteamUser.GetSteamID().m_SteamID;
                    _localCosmetics = PlayerCosmetics.FromSteamID(steamId);
                    CoopMod.Logger.LogInfo($"{LogPrefix} Generated cosmetics from Steam ID: {_localCosmetics}");
                }
                else
                {
                    _localCosmetics = PlayerCosmetics.Default;
                }
            }

            _localCosmetics = (_localCosmetics ?? PlayerCosmetics.Default).Clamped();
            LoadRecentColors();
            LoadSavedPresets();
        }

        public void SetLocalCosmetics(PlayerCosmetics cosmetics)
        {
            CommitLocalCosmetics(cosmetics, false);
        }

        /// <summary>
        /// Persists and broadcasts a wardrobe preview that was successfully applied
        /// immediately before this call, without forcing the same atlas work twice.
        /// </summary>
        public void CommitPreviewedLocalCosmetics(PlayerCosmetics cosmetics)
        {
            CommitLocalCosmetics(cosmetics, true);
        }

        private void CommitLocalCosmetics(PlayerCosmetics cosmetics, bool alreadyApplied)
        {
            _localCosmetics = (cosmetics ?? PlayerCosmetics.Default).Clamped();
            EnsureLocalPlayerDriver();
            if (!alreadyApplied)
                _localDriver?.SetCosmetics(_localCosmetics);

            var config = ModConfig.Instance;
            if (config != null)
            {
                config.PlayerPantsColor.Value = _localCosmetics.PantsColor;
                config.PlayerShirtColor.Value = _localCosmetics.ShirtColor;
                config.PlayerHairColor.Value = _localCosmetics.HairColor;
                config.PlayerSkinTone.Value = _localCosmetics.SkinTone;
                config.PlayerEyeColor.Value = _localCosmetics.EyeColor;
                config.PlayerPantsTone.Value = _localCosmetics.PantsTone;
                config.PlayerShirtTone.Value = _localCosmetics.ShirtTone;
                config.PlayerHairTone.Value = _localCosmetics.HairTone;
                config.PlayerSkinShade.Value = _localCosmetics.SkinShade;
                config.PlayerEyeTone.Value = _localCosmetics.EyeTone;
            }

            RememberColors(_localCosmetics, true);

            BroadcastLocalCosmetics();
            CoopMod.Logger.LogInfo($"{LogPrefix} Set local cosmetics: {_localCosmetics}");
        }

        public int[] GetRecentColors(CosmeticCategory category)
        {
            List<int> values = recentColors[(int)category];
            return values.ToArray();
        }

        public PlayerCosmetics[] GetSavedPresetSlots()
        {
            PlayerCosmetics[] result = new PlayerCosmetics[SavedPresetSlotCount];
            for (int index = 0; index < savedPresetSlots.Length; index++)
                result[index] = savedPresetSlots[index]?.Clone();
            return result;
        }

        public bool SaveSavedPresetSlot(int slotIndex, PlayerCosmetics cosmetics)
        {
            if (slotIndex < 0 || slotIndex >= savedPresetSlots.Length || cosmetics == null)
                return false;

            savedPresetSlots[slotIndex] = cosmetics.Clamped();
            PersistSavedPresets();
            return true;
        }

        public bool DeleteSavedPresetSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= savedPresetSlots.Length ||
                savedPresetSlots[slotIndex] == null)
                return false;

            savedPresetSlots[slotIndex] = null;
            PersistSavedPresets();
            return true;
        }

        private void LoadRecentColors()
        {
            foreach (List<int> values in recentColors)
                values.Clear();

            string encoded = ModConfig.Instance?.PlayerRecentCosmeticColors?.Value;
            if (!string.IsNullOrEmpty(encoded))
            {
                bool combinedSelections = encoded.StartsWith("v2:", StringComparison.Ordinal);
                if (combinedSelections)
                    encoded = encoded.Substring(3);
                string[] categories = encoded.Split('|');
                for (int categoryIndex = 0;
                     categoryIndex < recentColors.Length && categoryIndex < categories.Length;
                     categoryIndex++)
                {
                    CosmeticCategory category = (CosmeticCategory)categoryIndex;
                    string[] entries = categories[categoryIndex].Split(',');
                    foreach (string entry in entries)
                    {
                        if (!int.TryParse(entry, out int value))
                            continue;
                        if (!combinedSelections)
                        {
                            if (value < 0 || value >= PlayerCosmetics.GetOptionCount(category))
                                continue;
                            value = PlayerCosmetics.EncodeSelection(
                                value,
                                PlayerCosmetics.DEFAULT_TONE);
                        }
                        if (value < 0 ||
                            value >= PlayerCosmetics.GetSelectionCount(category) ||
                            recentColors[categoryIndex].Contains(value))
                        {
                            continue;
                        }

                        recentColors[categoryIndex].Add(value);
                    }
                }
            }

            RememberColors(_localCosmetics, false);
        }

        private void RememberColors(PlayerCosmetics cosmetics, bool persist)
        {
            if (cosmetics == null)
                return;

            for (int categoryIndex = 0; categoryIndex < recentColors.Length; categoryIndex++)
            {
                CosmeticCategory category = (CosmeticCategory)categoryIndex;
                int value = cosmetics.GetSelection(category);
                List<int> values = recentColors[categoryIndex];
                values.Remove(value);
                values.Insert(0, value);
                if (values.Count > 8)
                    values.RemoveRange(8, values.Count - 8);
            }

            if (!persist || ModConfig.Instance?.PlayerRecentCosmeticColors == null)
                return;

            string[] categories = new string[recentColors.Length];
            for (int categoryIndex = 0; categoryIndex < recentColors.Length; categoryIndex++)
                categories[categoryIndex] = string.Join(",", recentColors[categoryIndex]);
            ModConfig.Instance.PlayerRecentCosmeticColors.Value =
                "v2:" + string.Join("|", categories);
        }

        private void LoadSavedPresets()
        {
            Array.Clear(savedPresetSlots, 0, savedPresetSlots.Length);
            string encoded = ModConfig.Instance?.PlayerCosmeticPresets?.Value;
            if (string.IsNullOrEmpty(encoded))
                return;

            bool fixedSlots = encoded.StartsWith("v2:", StringComparison.Ordinal);
            bool legacySlots = encoded.StartsWith("v1:", StringComparison.Ordinal);
            if (fixedSlots || legacySlots)
                encoded = encoded.Substring(3);

            int nextLegacySlot = 0;
            string[] entries = encoded.Split(';');
            for (int entryIndex = 0; entryIndex < entries.Length; entryIndex++)
            {
                string entry = entries[entryIndex];
                if (string.IsNullOrEmpty(entry))
                    continue;
                try
                {
                    byte[] data = Convert.FromBase64String(entry);
                    if (data.Length < 10)
                        continue;
                    PlayerCosmetics preset = PlayerCosmetics.FromBytes(data).Clamped();
                    int slotIndex = fixedSlots ? entryIndex : nextLegacySlot++;
                    if (slotIndex >= savedPresetSlots.Length)
                        break;
                    savedPresetSlots[slotIndex] = preset;
                }
                catch (FormatException)
                {
                    CoopMod.Logger.LogWarning(
                        $"{LogPrefix} Ignored an invalid saved cosmetic preset");
                }
            }
            if (!fixedSlots)
                PersistSavedPresets();
        }

        private void PersistSavedPresets()
        {
            if (ModConfig.Instance?.PlayerCosmeticPresets == null)
                return;
            string[] entries = new string[savedPresetSlots.Length];
            for (int index = 0; index < savedPresetSlots.Length; index++)
            {
                entries[index] = savedPresetSlots[index] == null
                    ? string.Empty
                    : Convert.ToBase64String(savedPresetSlots[index].ToBytes());
            }
            ModConfig.Instance.PlayerCosmeticPresets.Value =
                "v2:" + string.Join(";", entries);
        }

        /// <summary>
        /// Applies an uncommitted wardrobe selection to this machine only. It does
        /// not write config or notify peers; SetLocalCosmetics is the commit path.
        /// </summary>
        public bool PreviewLocalCosmetics(PlayerCosmetics cosmetics)
        {
            EnsureLocalPlayerDriver();
            if (_localDriver == null)
                return false;

            _localDriver.SetCosmetics(
                (cosmetics ?? _localCosmetics ?? PlayerCosmetics.Default).Clamped());
            return _localDriver.LastApplySucceeded;
        }

        public void EnsureLocalPlayerDriver()
        {
            WorldGameObject player = MainGame.me?.player;
            if (player == null || player.gameObject == null)
                return;

            if (_localDriver != null && _localDriver.gameObject == player.gameObject)
                return;

            _localDriver = player.GetComponent<PlayerCosmeticsDriver>();
            if (_localDriver == null)
                _localDriver = player.gameObject.AddComponent<PlayerCosmeticsDriver>();

            _localDriver.SetCosmetics(_localCosmetics ?? PlayerCosmetics.Default);
            CoopMod.Logger.LogInfo($"{LogPrefix} Bound cosmetics driver to the local player");
        }

        private void Update()
        {
            if (Time.unscaledTime >= _nextLocalDriverCheck)
            {
                _nextLocalDriverCheck = Time.unscaledTime + 0.5f;
                EnsureLocalPlayerDriver();
            }

            if (loadingSynchronizationActive &&
                Time.unscaledTime >= nextLoadingSynchronizationRetryAt)
            {
                SynchronizeLoadingCosmetics();
            }
        }

        /// <summary>
        /// Starts the loading-screen appearance exchange. Saved preset slots stay local;
        /// only the active appearance is exchanged and tracked here.
        /// </summary>
        public void BeginLoadingSynchronization()
        {
            loadingExpectedPeers.Clear();
            remoteCosmeticsByPeer.Clear();
            loadingSynchronizationActive = true;
            nextLoadingSynchronizationRetryAt = 0f;
            RefreshLoadingExpectedPeers();
            SynchronizeLoadingCosmetics();

            CoopMod.Logger.LogInfo(
                $"{LogPrefix} Loading synchronization started for " +
                $"{loadingExpectedPeers.Count} remote keeper(s)");
        }

        public bool IsLoadingSynchronizationReady(
            int expectedRemotePlayers,
            out int receivedCount)
        {
            expectedRemotePlayers = Math.Max(0, expectedRemotePlayers);
            RefreshLoadingExpectedPeers();

            receivedCount = 0;
            foreach (ulong peerID in loadingExpectedPeers)
            {
                if (remoteCosmeticsByPeer.ContainsKey(peerID))
                    receivedCount++;
            }

            if (expectedRemotePlayers == 0)
                return true;

            return loadingExpectedPeers.Count >= expectedRemotePlayers &&
                   receivedCount >= expectedRemotePlayers;
        }

        public void CompleteLoadingSynchronization()
        {
            loadingSynchronizationActive = false;
            nextLoadingSynchronizationRetryAt = 0f;
            loadingExpectedPeers.Clear();
        }

        private void SynchronizeLoadingCosmetics()
        {
            nextLoadingSynchronizationRetryAt =
                Time.unscaledTime + LoadingSynchronizationRetrySeconds;
            RefreshLoadingExpectedPeers();

            BroadcastLocalCosmetics();
            foreach (ulong peerID in loadingExpectedPeers)
            {
                if (!remoteCosmeticsByPeer.ContainsKey(peerID))
                    RequestCosmeticsFrom(new CSteamID(peerID));
            }
        }

        private void RefreshLoadingExpectedPeers()
        {
            SteamLobbyManager lobby = SteamLobbyManager.Instance;
            if (!SteamManager.Initialized || lobby == null || !lobby.IsInLobby ||
                lobby.CurrentLobbyID == CSteamID.Nil)
            {
                return;
            }

            CSteamID localID = SteamUser.GetSteamID();
            int memberCount = lobby.GetLobbyMemberCount();
            for (int index = 0; index < memberCount; index++)
            {
                CSteamID memberID = SteamMatchmaking.GetLobbyMemberByIndex(
                    lobby.CurrentLobbyID,
                    index);
                if (memberID != CSteamID.Nil && memberID != localID)
                    loadingExpectedPeers.Add(memberID.m_SteamID);
            }
        }

        public void BroadcastLocalCosmetics()
        {
            if (!IsOnline) return;

            byte[] data = _localCosmetics.ToBytes();
            SteamP2PManager.Instance?.BroadcastCosmetics(data);
        }

        public void SendCosmeticsTo(CSteamID targetID)
        {
            byte[] data = _localCosmetics.ToBytes();
            SteamP2PManager.Instance?.SendCosmetics(targetID, data);
        }

        public void RequestCosmeticsFrom(CSteamID targetID)
        {
            SteamP2PManager.Instance?.RequestCosmetics(targetID);
        }

        public void SetRemotePlayerDriver(PlayerCosmeticsDriver driver)
        {
            _remoteDriver = driver;

            if (_remoteCosmetics != null && _remoteDriver != null)
            {
                _remoteDriver.SetCosmetics(_remoteCosmetics);
            }
        }

        public void SetRemotePlayerDriver(
            CSteamID peer,
            PlayerCosmeticsDriver driver)
        {
            if (peer == CSteamID.Nil)
            {
                SetRemotePlayerDriver(driver);
                return;
            }

            if (driver == null)
            {
                remoteDriversByPeer.Remove(peer.m_SteamID);
                return;
            }

            remoteDriversByPeer[peer.m_SteamID] = driver;
            if (remoteCosmeticsByPeer.TryGetValue(
                    peer.m_SteamID,
                    out PlayerCosmetics cosmetics))
            {
                driver.SetCosmetics(cosmetics);
            }

            if (OnlineCoopManager.Instance?.RemotePlayerSteamID == peer)
            {
                _remoteDriver = driver;
                if (_remoteCosmetics != null)
                    driver.SetCosmetics(_remoteCosmetics);
            }
        }

        public void RemoveRemotePlayerDriver(CSteamID peer)
        {
            if (peer == CSteamID.Nil)
                return;

            remoteDriversByPeer.Remove(peer.m_SteamID);
            remoteCosmeticsByPeer.Remove(peer.m_SteamID);
            if (OnlineCoopManager.Instance?.RemotePlayerSteamID == peer)
            {
                _remoteDriver = null;
                _remoteCosmetics = null;
            }
        }

        public void OnCoopStarted()
        {
            CSteamID primaryPeer =
                OnlineCoopManager.Instance?.RemotePlayerSteamID ?? CSteamID.Nil;
            _remoteCosmetics = primaryPeer != CSteamID.Nil &&
                remoteCosmeticsByPeer.TryGetValue(
                    primaryPeer.m_SteamID,
                    out PlayerCosmetics receivedCosmetics)
                    ? receivedCosmetics
                    : PlayerCosmetics.Player2Default;

            StartCoroutine(SendCosmeticsAfterDelay(0.5f));
        }

        private System.Collections.IEnumerator SendCosmeticsAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            BroadcastLocalCosmetics();
        }

        public void OnCoopEnded()
        {
            CompleteLoadingSynchronization();
            _remoteCosmetics = null;
            _remoteDriver = null;
            remoteCosmeticsByPeer.Clear();
            remoteDriversByPeer.Clear();
        }

        private void OnCosmeticsReceived(CSteamID senderID, byte[] cosmeticsData)
        {
            try
            {
                _remoteCosmetics = PlayerCosmetics.FromBytes(cosmeticsData);
                remoteCosmeticsByPeer[senderID.m_SteamID] = _remoteCosmetics;
                string senderName = SteamFriends.GetFriendPersonaName(senderID);

                CoopMod.Logger.LogInfo($"{LogPrefix} Received cosmetics from {senderName}: {_remoteCosmetics}");

                if (remoteDriversByPeer.TryGetValue(
                        senderID.m_SteamID,
                        out PlayerCosmeticsDriver driver) &&
                    driver != null)
                {
                    driver.SetCosmetics(_remoteCosmetics);
                }
                else if (_remoteDriver != null &&
                         OnlineCoopManager.Instance?.RemotePlayerSteamID == senderID)
                {
                    _remoteDriver.SetCosmetics(_remoteCosmetics);
                }

                if (loadingSynchronizationActive &&
                    IsLoadingSynchronizationReady(
                        loadingExpectedPeers.Count,
                        out int receivedCount))
                {
                    CoopMod.Logger.LogInfo(
                        $"{LogPrefix} Loading synchronization complete " +
                        $"({receivedCount}/{loadingExpectedPeers.Count})");
                    loadingSynchronizationActive = false;
                }
            }
            catch (Exception ex)
            {
                CoopMod.Logger.LogError($"{LogPrefix} Error processing cosmetics: {ex.Message}");
            }
        }

        private void OnCosmeticsRequested(CSteamID senderID)
        {
            string senderName = SteamFriends.GetFriendPersonaName(senderID);
            CoopMod.Logger.LogInfo($"{LogPrefix} Cosmetics requested by {senderName}");

            SendCosmeticsTo(senderID);
        }

        public void CyclePantsColor(int direction = 1)
        {
            PlayerCosmetics cosmetics = _localCosmetics.Clone();
            cosmetics.PantsColor =
                (_localCosmetics.PantsColor + direction + PlayerCosmetics.MAX_PANTS_OPTIONS) %
                PlayerCosmetics.MAX_PANTS_OPTIONS;
            SetLocalCosmetics(cosmetics);
        }

        public void CycleShirtColor(int direction = 1)
        {
            PlayerCosmetics cosmetics = _localCosmetics.Clone();
            cosmetics.ShirtColor =
                (_localCosmetics.ShirtColor + direction + PlayerCosmetics.MAX_SHIRT_OPTIONS) %
                PlayerCosmetics.MAX_SHIRT_OPTIONS;
            SetLocalCosmetics(cosmetics);
        }

        public void CycleHairColor(int direction = 1)
        {
            PlayerCosmetics cosmetics = _localCosmetics.Clone();
            cosmetics.HairColor =
                (_localCosmetics.HairColor + direction + PlayerCosmetics.MAX_HAIR_OPTIONS) %
                PlayerCosmetics.MAX_HAIR_OPTIONS;
            SetLocalCosmetics(cosmetics);
        }

        public void RandomizeCosmetics()
        {
            SetLocalCosmetics(PlayerCosmetics.Random());
        }
    }
}

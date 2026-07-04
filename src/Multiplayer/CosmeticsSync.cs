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
        public static CosmeticsSync Instance => GetInstance<CosmeticsSync>();

        private PlayerCosmetics _localCosmetics;
        private PlayerCosmetics _remoteCosmetics;
        private PlayerCosmeticsDriver _remoteDriver;

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
        }

        public void SetLocalCosmetics(PlayerCosmetics cosmetics)
        {
            _localCosmetics = cosmetics ?? PlayerCosmetics.Default;

            var config = ModConfig.Instance;
            if (config != null)
            {
                config.PlayerPantsColor.Value = _localCosmetics.PantsColor;
                config.PlayerShirtColor.Value = _localCosmetics.ShirtColor;
                config.PlayerHairColor.Value = _localCosmetics.HairColor;
                config.PlayerSkinTone.Value = _localCosmetics.SkinTone;
                config.PlayerEyeColor.Value = _localCosmetics.EyeColor;
            }

            BroadcastLocalCosmetics();
            CoopMod.Logger.LogInfo($"{LogPrefix} Set local cosmetics: {_localCosmetics}");
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

        public void OnCoopStarted()
        {
            _remoteCosmetics = PlayerCosmetics.Player2Default;

            StartCoroutine(SendCosmeticsAfterDelay(0.5f));
        }

        private System.Collections.IEnumerator SendCosmeticsAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            BroadcastLocalCosmetics();
        }

        public void OnCoopEnded()
        {
            _remoteCosmetics = null;
            _remoteDriver = null;
        }

        private void OnCosmeticsReceived(CSteamID senderID, byte[] cosmeticsData)
        {
            try
            {
                _remoteCosmetics = PlayerCosmetics.FromBytes(cosmeticsData);
                string senderName = SteamFriends.GetFriendPersonaName(senderID);

                CoopMod.Logger.LogInfo($"{LogPrefix} Received cosmetics from {senderName}: {_remoteCosmetics}");

                if (_remoteDriver != null)
                {
                    _remoteDriver.SetCosmetics(_remoteCosmetics);
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
            var cosmetics = new PlayerCosmetics
            {
                PantsColor = (_localCosmetics.PantsColor + direction + PlayerCosmetics.MAX_PANTS_OPTIONS) % PlayerCosmetics.MAX_PANTS_OPTIONS,
                ShirtColor = _localCosmetics.ShirtColor,
                HairColor = _localCosmetics.HairColor,
                SkinTone = _localCosmetics.SkinTone,
                EyeColor = _localCosmetics.EyeColor,
            };
            SetLocalCosmetics(cosmetics);
        }

        public void CycleShirtColor(int direction = 1)
        {
            var cosmetics = new PlayerCosmetics
            {
                PantsColor = _localCosmetics.PantsColor,
                ShirtColor = (_localCosmetics.ShirtColor + direction + PlayerCosmetics.MAX_SHIRT_OPTIONS) % PlayerCosmetics.MAX_SHIRT_OPTIONS,
                HairColor = _localCosmetics.HairColor,
                SkinTone = _localCosmetics.SkinTone,
                EyeColor = _localCosmetics.EyeColor,
            };
            SetLocalCosmetics(cosmetics);
        }

        public void CycleHairColor(int direction = 1)
        {
            var cosmetics = new PlayerCosmetics
            {
                PantsColor = _localCosmetics.PantsColor,
                ShirtColor = _localCosmetics.ShirtColor,
                HairColor = (_localCosmetics.HairColor + direction + PlayerCosmetics.MAX_HAIR_OPTIONS) % PlayerCosmetics.MAX_HAIR_OPTIONS,
                SkinTone = _localCosmetics.SkinTone,
                EyeColor = _localCosmetics.EyeColor,
            };
            SetLocalCosmetics(cosmetics);
        }

        public void RandomizeCosmetics()
        {
            SetLocalCosmetics(PlayerCosmetics.Random());
        }
    }
}

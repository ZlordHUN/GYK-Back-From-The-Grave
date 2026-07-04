using System;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using Steamworks;
using HarmonyLib;
using GraveyardKeeperCoop.Network;

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
        private const int MaxCatchUpDaysPerPacket = 14;
        private float lastSendTime = 0f;
        private static MethodInfo onEndOfDayMethod;

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
        }

        private void Update()
        {
            if (!IsSyncEnabled) return;

            if (!IsOnline) return;

            if (!IsHost)
            {
                DisableClientAutoTime();
                return;
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

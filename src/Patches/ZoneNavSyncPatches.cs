using HarmonyLib;
using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch]
    internal static class ZoneNavSyncPatches
    {
        private static void MarkDirty()
        {
            var sync = GraveyardKeeperCoop.Multiplayer.ZoneNavSync.Instance;
            if (sync == null) return;
            sync.MarkDirty();
        }

        [HarmonyPatch(typeof(GameSave), "OnEnteredWorldZone")]
        [HarmonyPostfix]
        internal static void OnEnteredWorldZone_Postfix(WorldZone z)
        {
            if (z == null) return;
            var sync = GraveyardKeeperCoop.Multiplayer.ZoneNavSync.Instance;
            if (sync == null) return;
            sync.SendZoneDiscoveredEvent(z.id);
            MarkDirty();
        }

        [HarmonyPatch(typeof(WorldZone), "EnableWorldZone")]
        [HarmonyPostfix]
        internal static void EnableWorldZone_Postfix(WorldZone __instance)
        {
            if (__instance == null) return;
            var sync = GraveyardKeeperCoop.Multiplayer.ZoneNavSync.Instance;
            if (sync == null) return;
            if (sync.IsApplyingRemoteZoneState) return;
            sync.SendZoneEnabledEvent(__instance.id, true);
            MarkDirty();
        }

        [HarmonyPatch(typeof(WorldZone), "DisableWorldZone")]
        [HarmonyPostfix]
        internal static void DisableWorldZone_Postfix(WorldZone __instance)
        {
            if (__instance == null) return;
            var sync = GraveyardKeeperCoop.Multiplayer.ZoneNavSync.Instance;
            if (sync == null) return;
            if (sync.IsApplyingRemoteZoneState) return;
            sync.SendZoneEnabledEvent(__instance.id, false);
            MarkDirty();
        }
    }

    [HarmonyPatch]
    internal static class TeleportLockPatches
    {
        [HarmonyPatch(typeof(Item), "SetParam", new System.Type[] { typeof(string), typeof(float) })]
        [HarmonyPrefix]
        internal static void SetParam_Prefix(Item __instance, string param_name, float value)
        {
            if (param_name != "lock_tp" && param_name != "lock_tp_param") return;
            if (__instance != MainGame.me?.player?.data) return;

            var sync = GraveyardKeeperCoop.Multiplayer.ZoneNavSync.Instance;
            if (sync == null) return;
            if (sync.IsApplyingRemoteTeleportLock) return;

            bool currentLocked = __instance.GetParam("lock_tp", 0f) > 0.5f;
            int currentLockParam = (int)__instance.GetParam("lock_tp_param", 0f);

            if (param_name == "lock_tp")
            {
                bool locked = value > 0.5f;
                if (locked == currentLocked) return;
                sync.SendTeleportLockEvent(locked, currentLockParam);
            }
            else if (param_name == "lock_tp_param")
            {
                int lockParam = (int)value;
                if (lockParam == currentLockParam) return;
                sync.SendTeleportLockEvent(currentLocked, lockParam);
            }
        }
    }

    [HarmonyPatch]
    internal static class GDZonePatches
    {
        // Ownership: OnlineZonePatches may suppress remote online proxy triggers first,
        // and LocalCoopDialoguePatches records local co-op zone ownership. ZoneNavSync
        // only observes successful local-player zone transitions and emits navigation sync.
        [HarmonyPatch(typeof(GDZone), "OnTriggerEnter2D")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        internal static void GDZone_Enter_Postfix(GDZone __instance, Collider2D collision)
        {
            if (__instance == null) return;
            if (collision == null) return;
            var wgo = collision.gameObject.GetComponentInParent<WorldGameObject>();
            if (wgo == null || !wgo.is_player) return;
            if (wgo != MainGame.me?.player) return;

            var sync = GraveyardKeeperCoop.Multiplayer.ZoneNavSync.Instance;
            if (sync == null) return;

            string ovrMusic = (__instance.on_enter != null) ? (__instance.on_enter.ovr_music ?? "") : "";
            sync.SendGDZoneEvent(__instance.name, 0, ovrMusic);
        }

        [HarmonyPatch(typeof(GDZone), "OnTriggerExit2D")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        internal static void GDZone_Exit_Postfix(GDZone __instance, Collider2D collision)
        {
            if (__instance == null) return;
            if (collision == null) return;
            var wgo = collision.gameObject.GetComponentInParent<WorldGameObject>();
            if (wgo == null || !wgo.is_player) return;
            if (wgo != MainGame.me?.player) return;

            var sync = GraveyardKeeperCoop.Multiplayer.ZoneNavSync.Instance;
            if (sync == null) return;

            string ovrMusic = (__instance.on_enter != null) ? (__instance.on_enter.ovr_music ?? "") : "";
            sync.SendGDZoneEvent(__instance.name, 1, ovrMusic);
        }
    }
}

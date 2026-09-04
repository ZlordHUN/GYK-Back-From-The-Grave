using System;
using HarmonyLib;
using UnityEngine;
using GraveyardKeeperCoop.LocalCoop;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch]
    internal static class CombatSyncPatches
    {
        [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.hp), MethodType.Setter)]
        [HarmonyPrefix]
        internal static void HpSet_Prefix(WorldGameObject __instance, float value)
        {
            if (__instance == null || __instance.is_player) return;
            if (MainGame.me?.player == __instance) return;
            if (!IsSyncEnabled()) return;
            if (IsApplyingRemoteState()) return;

            float currentHp = __instance.hp;
            if (Mathf.Approximately(currentHp, value)) return;

            var sync = GraveyardKeeperCoop.Multiplayer.CombatSync.Instance;
            if (sync == null) return;
            sync.QueueWgoHpChange(__instance.unique_id, value);
        }

        [HarmonyPatch(typeof(WorldGameObject), "DoPreZeroHPActivity")]
        [HarmonyPrefix]
        internal static void DoPreZeroHPActivity_Prefix(WorldGameObject __instance)
        {
            if (__instance == null) return;
            if (__instance.is_player) return;
            if (!IsSyncEnabled()) return;
            if (IsApplyingRemoteState()) return;

            var sync = GraveyardKeeperCoop.Multiplayer.CombatSync.Instance;
            if (sync == null) return;
            sync.SendWgoDeathAnimationEvent(__instance.unique_id);
        }

        // Online drop sync is an observer of accepted local collections. Local co-op
        // may handle some collections itself; those paths call NotifyLocalDropCollectedFromLocalPath.
        [HarmonyPatch(typeof(DropResGameObject), "CollectDrop")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        internal static void CollectDrop_Postfix(DropResGameObject __instance, WorldGameObject player)
        {
            if (__instance == null || __instance.res == null) return;
            if (!IsSyncEnabled()) return;
            if (!__instance.is_collected) return;
            if (!IsAcceptedLocalCollector(player)) return;

            NotifyLocalDropCollectedFromLocalPath(__instance);
        }

        [HarmonyPatch(typeof(BaseCharacterComponent), "TryOtherInteractions")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Low)]
        internal static void TryOtherInteractions_Prefix(BaseCharacterComponent __instance, out DropResGameObject __state)
        {
            __state = null;
            if (!IsAcceptedLocalCollector(__instance?.wgo)) return;

            DropResGameObject highlighted = DropResGameObject.currently_higlighted_obj;
            if (highlighted != null && __instance != null && highlighted.CanPickupWithInteraction(__instance))
            {
                __state = highlighted;
            }
        }

        [HarmonyPatch(typeof(BaseCharacterComponent), "TryOtherInteractions")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        internal static void TryOtherInteractions_Postfix(BaseCharacterComponent __instance, bool __result, DropResGameObject __state)
        {
            if (!__result || __state == null || !IsSyncEnabled())
                return;
            if (!IsAcceptedLocalCollector(__instance?.wgo))
                return;

            NotifyLocalDropCollectedFromLocalPath(__state);
        }

        [HarmonyPatch(typeof(DropResHint), "LateUpdate")]
        [HarmonyPrefix]
        internal static bool DropResHint_LateUpdate_Prefix(DropResHint __instance)
        {
            return __instance != null && __instance.transform != null;
        }

        [HarmonyPatch(typeof(DropResGameObject), "Drop", new Type[]
        {
            typeof(Vector3),
            typeof(Item),
            typeof(Transform),
            typeof(Direction),
            typeof(float),
            typeof(int),
            typeof(bool),
            typeof(bool)
        })]
        [HarmonyPostfix]
        internal static void Drop_Postfix(DropResGameObject __result)
        {
            OnLocalDropCreated(__result);
        }

        [HarmonyPatch(typeof(DropResGameObject), "DropAndFly", new Type[]
        {
            typeof(Vector3),
            typeof(Item),
            typeof(Transform),
            typeof(Vector2),
            typeof(bool)
        })]
        [HarmonyPostfix]
        internal static void DropAndFly_Postfix(DropResGameObject __result)
        {
            OnLocalDropCreated(__result);
        }

        [HarmonyPatch(typeof(DropResGameObject), "DoDrop", new Type[]
        {
            typeof(Vector3),
            typeof(Item),
            typeof(Transform),
            typeof(Direction),
            typeof(float),
            typeof(int),
            typeof(bool)
        })]
        [HarmonyPostfix]
        internal static void DoDrop_Postfix(DropResGameObject __result)
        {
            OnLocalDropCreated(__result);
        }

        [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.progress), MethodType.Setter)]
        [HarmonyPrefix]
        internal static void ProgressSet_Prefix(WorldGameObject __instance, float value)
        {
            if (__instance == null || __instance.is_player) return;
            if (MainGame.me?.player == __instance) return;
            if (!IsSyncEnabled()) return;
            if (IsApplyingRemoteState()) return;

            float currentProgress = __instance.progress;
            if (Mathf.Approximately(currentProgress, value)) return;

            var sync = GraveyardKeeperCoop.Multiplayer.CombatSync.Instance;
            if (sync == null) return;
            sync.QueueWgoProgressChange(__instance.unique_id, value);
        }

        [HarmonyPatch(typeof(Item), nameof(Item.durability), MethodType.Setter)]
        [HarmonyPrefix]
        internal static void ItemDurabilitySet_Prefix(Item __instance, float value)
        {
            if (__instance == null) return;
            if (!IsSyncEnabled()) return;
            if (IsApplyingRemoteState()) return;

            var wgo = FindWgoByItem(__instance);
            if (wgo == null || wgo.is_player) return;

            float currentDurability = __instance.durability;
            if (Mathf.Approximately(currentDurability, value)) return;

            var sync = GraveyardKeeperCoop.Multiplayer.CombatSync.Instance;
            if (sync == null) return;
            sync.QueueWgoDurabilityChange(wgo.unique_id, Mathf.Clamp01(value));
        }

        [HarmonyPatch(typeof(BaseCharacterComponent), "OnWasDamaged")]
        [HarmonyPostfix]
        internal static void OnWasDamaged_Postfix(BaseCharacterComponent __instance, float damage_direction)
        {
            if (__instance == null) return;
            if (!IsSyncEnabled()) return;
            if (IsApplyingRemoteState()) return;

            var wgo = __instance.wgo;
            if (wgo == null || wgo.is_player) return;
            if (MainGame.me?.player == wgo) return;

            var sync = GraveyardKeeperCoop.Multiplayer.CombatSync.Instance;
            if (sync == null) return;
            sync.SendWgoHitReactionEvent(wgo.unique_id, damage_direction);
        }

        private static WorldGameObject FindWgoByItem(Item item)
        {
            if (MainGame.me?.player?.data == item) return MainGame.me.player;
            if (WorldMap.objs == null) return null;

            for (int i = 0; i < WorldMap.objs.Count; i++)
            {
                var wgo = WorldMap.objs[i];
                if (wgo != null && wgo.data == item)
                    return wgo;
            }
            return null;
        }

        private static void OnLocalDropCreated(DropResGameObject drop)
        {
            if (drop == null || !IsSyncEnabled()) return;

            var sync = GraveyardKeeperCoop.Multiplayer.CombatSync.Instance;
            if (sync == null) return;
            sync.NotifyLocalDropCreated(drop);
        }

        internal static void NotifyLocalDropCollectedFromLocalPath(DropResGameObject drop)
        {
            if (drop == null || drop.res == null || !IsSyncEnabled()) return;

            var sync = GraveyardKeeperCoop.Multiplayer.CombatSync.Instance;
            if (sync == null) return;
            sync.NotifyLocalDropCollected(drop);
        }

        private static bool IsAcceptedLocalCollector(WorldGameObject player)
        {
            if (player == null)
                return false;

            if (player == MainGame.me?.player)
                return true;

            var localCoop = LocalCoopManager.Instance;
            return localCoop != null
                && localCoop.IsLocalCoopEnabled
                && (player == localCoop.Player1?.wgo || player == localCoop.Player2?.wgo);
        }

        private static bool IsSyncEnabled()
        {
            var onlineCoop = GraveyardKeeperCoop.Network.OnlineCoopManager.Instance;
            return onlineCoop != null && onlineCoop.IsOnlineCoopEnabled;
        }

        private static bool IsApplyingRemoteState()
        {
            return GraveyardKeeperCoop.Multiplayer.CombatSync.IsApplyingRemoteState ||
                   GraveyardKeeperCoop.Multiplayer.WGOStateSync.IsProcessingRemote ||
                   GraveyardKeeperCoop.Multiplayer.SyncBehaviour.GlobalRemoteApplyActive;
        }
    }
}

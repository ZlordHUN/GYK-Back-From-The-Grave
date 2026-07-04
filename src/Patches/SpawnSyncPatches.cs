using HarmonyLib;
using System;
using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    [HarmonyPatch]
    internal static class SpawnSyncPatches
    {
        internal struct ReplacementState
        {
            public long UniqueId;
            public string ObjId;
        }

        [HarmonyPatch(typeof(WorldMap), "OnAddNewWGO")]
        [HarmonyPostfix]
        internal static void OnAddNewWGO_Postfix(WorldGameObject wgo)
        {
            if (wgo == null) return;
            GraveyardKeeperCoop.Multiplayer.WGORegistry.Instance?.Register(wgo);

            if (!IsSyncEnabled()) return;
            var onlineCoop = GraveyardKeeperCoop.Network.OnlineCoopManager.Instance;
            if (onlineCoop == null) return;

            if (wgo.is_player || wgo.GetComponent<PlayerComponent>() != null) return;
            if (string.IsNullOrEmpty(wgo.obj_id) || wgo.obj_id == "0") return;
            if (wgo.is_removed) return;
            if (MainGame.me?.world_root != null && !wgo.transform.IsChildOf(MainGame.me.world_root)) return;

            var sync = GraveyardKeeperCoop.Multiplayer.SpawnSync.Instance;
            if (sync == null) return;
            sync.SendWgoSpawned(wgo);
        }

        [HarmonyPatch(
            typeof(WorldGameObject),
            nameof(WorldGameObject.ReplaceWithObject))]
        [HarmonyPrefix]
        internal static void ReplaceWithObject_Prefix(
            WorldGameObject __instance,
            out ReplacementState __state)
        {
            __state = new ReplacementState
            {
                UniqueId = __instance?.unique_id ?? 0L,
                ObjId = __instance?.obj_id ?? string.Empty
            };
        }

        [HarmonyPatch(
            typeof(WorldGameObject),
            nameof(WorldGameObject.ReplaceWithObject))]
        [HarmonyPostfix]
        internal static void ReplaceWithObject_Postfix(
            WorldGameObject __instance,
            ReplacementState __state)
        {
            GraveyardKeeperCoop.Multiplayer.SpawnSync.Instance
                ?.NotifyWgoReplaced(
                    __state.UniqueId,
                    __state.ObjId,
                    __instance);
        }

        /// <summary>
        /// Several story Flow nodes move an existing NPC directly and then call
        /// OnCameToGDPoint. No WGO is created, so the normal spawn hook cannot see
        /// that transition. Relay client-initiated Flow placements to the host.
        ///
        /// MovementComponent also calls OnCameToGDPoint for ordinary pathfinding;
        /// the stack check deliberately excludes those arrivals.
        /// </summary>
        [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.OnCameToGDPoint))]
        [HarmonyPostfix]
        internal static void OnCameToGDPoint_Postfix(
            WorldGameObject __instance,
            GDPoint p)
        {
            if (__instance == null || p == null ||
                GraveyardKeeperCoop.Multiplayer.SpawnSync.IsApplyingCanonicalFlowPlacement ||
                !IsCalledFromFlowNode())
            {
                return;
            }

            GraveyardKeeperCoop.Multiplayer.SpawnSync.Instance
                ?.RequestCanonicalFlowPlacement(__instance, p);
        }

        private static bool IsCalledFromFlowNode()
        {
            var trace = new System.Diagnostics.StackTrace(1, false);
            System.Diagnostics.StackFrame[] frames = trace.GetFrames();
            if (frames == null)
                return false;

            int count = Math.Min(frames.Length, 24);
            for (int i = 0; i < count; i++)
            {
                Type declaringType = frames[i].GetMethod()?.DeclaringType;
                string fullName = declaringType?.FullName;
                if (!string.IsNullOrEmpty(fullName) &&
                    fullName.IndexOf(
                        "FlowCanvas.Nodes.Flow_",
                        StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSyncEnabled()
        {
            var onlineCoop = GraveyardKeeperCoop.Network.OnlineCoopManager.Instance;
            return onlineCoop != null && onlineCoop.IsOnlineCoopEnabled;
        }
    }
}

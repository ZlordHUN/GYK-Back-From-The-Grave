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

        internal struct BuildPlacementState
        {
            public WorldGameObject Candidate;
        }

        internal struct InteractionEventsState
        {
            public bool Track;
            public string Fingerprint;
        }

        private static WorldGameObject buildPlacementInProgress;
        private static bool creatingFloatingObject;

        [HarmonyPatch(typeof(WorldMap), "OnAddNewWGO")]
        [HarmonyPrefix]
        internal static void OnAddNewWGO_Prefix(
            WorldGameObject wgo,
            out bool __state)
        {
            __state = false;
            if (wgo == null || wgo.unique_id <= 0L)
                return;

            WorldGameObject registered =
                WorldMap.GetWorldGameObjectByUniqueId(
                    wgo.unique_id,
                    false);
            __state = registered == wgo;
        }

        [HarmonyPatch(typeof(WorldMap), "OnAddNewWGO")]
        [HarmonyPostfix]
        internal static void OnAddNewWGO_Postfix(
            WorldGameObject wgo,
            bool __state)
        {
            if (wgo == null) return;
            if (wgo.GetComponent<GraveyardKeeperCoop.Multiplayer.RemoteBuildingPreviewMarker>() != null)
                return;
            GraveyardKeeperCoop.Multiplayer.WGORegistry.Instance?.Register(wgo);

            // Building previews are registered as ordinary WGOs before the player chooses
            // their final grid position. They receive their own explicit commit message from
            // DoPlace; broadcasting here would serialize the preview position.
            if (creatingFloatingObject ||
                wgo.GetComponent<FloatingWorldGameObject>() != null ||
                buildPlacementInProgress == wgo)
            {
                return;
            }

            // RestoreFromSerializedObject calls SetObject -> OnAddNewWGO. Remote/canonical
            // restores are already being relayed by their owning protocol and must not emit a
            // nested host spawn while they are only half restored.
            if (GraveyardKeeperCoop.Multiplayer.SpawnSync.IsApplyingCanonicalWgoState ||
                GraveyardKeeperCoop.Multiplayer.WGOStateSync.IsProcessingRemote)
            {
                return;
            }

            // SetObject calls OnAddNewWGO even when this exact WGO is already
            // registered. Broadcasting those calls repeatedly made clients
            // restore and re-hide an object that had already spawned.
            if (__state) return;

            if (!IsSyncEnabled()) return;
            var onlineCoop = GraveyardKeeperCoop.Network.OnlineCoopManager.Instance;
            if (onlineCoop == null) return;

            if (wgo.is_player || wgo.GetComponent<PlayerComponent>() != null) return;
            if (string.IsNullOrEmpty(wgo.obj_id) || wgo.obj_id == "0") return;
            if (wgo.is_removed) return;
            if (MainGame.me?.world_root != null && !wgo.transform.IsChildOf(MainGame.me.world_root)) return;

            var sync = GraveyardKeeperCoop.Multiplayer.SpawnSync.Instance;
            if (sync == null) return;
            sync.QueueHostWgoSpawn(wgo);
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
            nameof(WorldGameObject.AddInteractionEvent))]
        [HarmonyPostfix]
        internal static void AddInteractionEvent_Postfix(
            WorldGameObject __instance)
        {
            GraveyardKeeperCoop.Multiplayer.SpawnSync.Instance
                ?.NotifyInteractionEventsChanged(__instance);
        }

        [HarmonyPatch(
            typeof(WorldGameObject),
            nameof(WorldGameObject.Interact))]
        [HarmonyPrefix]
        internal static void Interact_Prefix(
            WorldGameObject __instance,
            out InteractionEventsState __state)
        {
            bool track = IsSyncEnabled() &&
                __instance?.custom_interaction_events != null &&
                __instance.custom_interaction_events.Count > 0;
            __state = new InteractionEventsState
            {
                Track = track,
                Fingerprint = track
                    ? GetInteractionEventsFingerprint(__instance)
                    : string.Empty
            };
        }

        [HarmonyPatch(
            typeof(WorldGameObject),
            nameof(WorldGameObject.Interact))]
        [HarmonyPostfix]
        internal static void Interact_Postfix(
            WorldGameObject __instance,
            InteractionEventsState __state)
        {
            if (!__state.Track || string.Equals(
                    __state.Fingerprint,
                    GetInteractionEventsFingerprint(__instance),
                    StringComparison.Ordinal))
            {
                return;
            }

            GraveyardKeeperCoop.Multiplayer.SpawnSync.Instance
                ?.NotifyInteractionEventsChanged(__instance);
        }

        [HarmonyPatch(
            typeof(WorldGameObject),
            nameof(WorldGameObject.ReplaceWithObject))]
        [HarmonyPostfix]
        internal static void ReplaceWithObject_Postfix(
            WorldGameObject __instance,
            ReplacementState __state)
        {
            // DoPlace commonly changes a committed blueprint to its *_place object. The
            // placement transaction carries that final state, so sending the normal
            // replacement request as well would create/canonicalize the same WGO twice.
            if (buildPlacementInProgress == __instance)
                return;

            GraveyardKeeperCoop.Multiplayer.SpawnSync.Instance
                ?.NotifyWgoReplaced(
                    __state.UniqueId,
                    __state.ObjId,
                    __instance);
        }

        [HarmonyPatch(
            typeof(WorldGameObject),
            nameof(WorldGameObject.DrawPuffFX))]
        [HarmonyPrefix]
        internal static bool DrawPuffFx_Prefix(
            WorldGameObject __instance,
            Bounds? bounds)
        {
            var sync = GraveyardKeeperCoop.Multiplayer.SpawnSync.Instance;
            return sync == null || sync.NotifyLocalPuffFx(__instance, bounds);
        }

        [HarmonyPatch(typeof(FloatingWorldGameObject), nameof(FloatingWorldGameObject.CreateFloatingWorldObjectById))]
        [HarmonyPrefix]
        internal static void CreateFloatingWorldObjectById_Prefix()
        {
            creatingFloatingObject = true;
        }

        [HarmonyPatch(typeof(FloatingWorldGameObject), nameof(FloatingWorldGameObject.CreateFloatingWorldObjectById))]
        [HarmonyPostfix]
        internal static void CreateFloatingWorldObjectById_Postfix(
            FloatingWorldGameObject __result)
        {
            try
            {
                GraveyardKeeperCoop.Multiplayer.SpawnSync.Instance
                    ?.PrepareLocalBuildingPreview(__result?.wobj);
            }
            finally
            {
                creatingFloatingObject = false;
            }
        }

        [HarmonyPatch(typeof(FloatingWorldGameObject), nameof(FloatingWorldGameObject.CreateFloatingWorldObjectById))]
        [HarmonyFinalizer]
        internal static Exception CreateFloatingWorldObjectById_Finalizer(
            Exception __exception)
        {
            creatingFloatingObject = false;
            return __exception;
        }

        [HarmonyPatch(typeof(FloatingWorldGameObject), nameof(FloatingWorldGameObject.MoveCurrentFloatingObject))]
        [HarmonyPostfix]
        internal static void MoveCurrentFloatingObject_Postfix()
        {
            NotifyCurrentBuildingPreviewChanged();
        }

        [HarmonyPatch(typeof(FloatingWorldGameObject), nameof(FloatingWorldGameObject.RotateCurrentFloatingObject))]
        [HarmonyPostfix]
        internal static void RotateCurrentFloatingObject_Postfix()
        {
            NotifyCurrentBuildingPreviewChanged();
        }

        [HarmonyPatch(typeof(FloatingWorldGameObject), nameof(FloatingWorldGameObject.MoveCurrentByDir))]
        [HarmonyPostfix]
        internal static void MoveCurrentByDir_Postfix()
        {
            NotifyCurrentBuildingPreviewChanged();
        }

        [HarmonyPatch(typeof(FloatingWorldGameObject), nameof(FloatingWorldGameObject.StopCurrentFloating))]
        [HarmonyPrefix]
        internal static void StopCurrentFloating_Prefix()
        {
            GraveyardKeeperCoop.Multiplayer.SpawnSync.Instance
                ?.NotifyBuildingPreviewEnded(
                    FloatingWorldGameObject.cur_floating?.wobj);
        }

        [HarmonyPatch(typeof(BuildModeLogics), "DoPlace")]
        [HarmonyPrefix]
        internal static void DoPlace_Prefix(out BuildPlacementState __state)
        {
            __state = new BuildPlacementState
            {
                Candidate = FloatingWorldGameObject.cur_floating?.wobj
            };
            buildPlacementInProgress = __state.Candidate;
        }

        [HarmonyPatch(typeof(BuildModeLogics), "DoPlace")]
        [HarmonyPostfix]
        internal static void DoPlace_Postfix(BuildPlacementState __state)
        {
            try
            {
                WorldGameObject placed = __state.Candidate;
                if (placed == null || !placed.just_built || placed.is_removed)
                    return;

                GraveyardKeeperCoop.Multiplayer.SpawnSync.Instance
                    ?.NotifyBuildingPlaced(placed);
            }
            finally
            {
                if (buildPlacementInProgress == __state.Candidate)
                    buildPlacementInProgress = null;
            }
        }

        [HarmonyPatch(typeof(BuildModeLogics), "DoPlace")]
        [HarmonyFinalizer]
        internal static Exception DoPlace_Finalizer(
            Exception __exception,
            BuildPlacementState __state)
        {
            if (buildPlacementInProgress == __state.Candidate)
                buildPlacementInProgress = null;
            return __exception;
        }

        [HarmonyPatch(typeof(MainGame), nameof(MainGame.EnterBuildMode))]
        [HarmonyPostfix]
        internal static void EnterBuildMode_Postfix()
        {
            GraveyardKeeperCoop.Multiplayer.SpawnSync.Instance
                ?.NotifyLocalBuildModeEntered();
        }

        [HarmonyPatch(typeof(MainGame), nameof(MainGame.ExitBuildMode))]
        [HarmonyPostfix]
        internal static void ExitBuildMode_Postfix()
        {
            GraveyardKeeperCoop.Multiplayer.SpawnSync.Instance
                ?.NotifyLocalBuildModeExited();
        }

        [HarmonyPatch(typeof(BuildModeLogics), nameof(BuildModeLogics.SetCurrentBuildZone))]
        [HarmonyPostfix]
        internal static void SetCurrentBuildZone_Postfix(
            string zone_id,
            string custom_sub_zone)
        {
            GraveyardKeeperCoop.Multiplayer.SpawnSync.Instance
                ?.NotifyLocalBuildZoneChanged(
                    zone_id,
                    custom_sub_zone);
        }

        /// <summary>
        /// Repair saves written before completed client-owned NPC routes released
        /// their observer-side chunk lock. A real in-progress route serializes a
        /// non-None movement state, so a locked NPC restored as stationary is stale.
        /// </summary>
        [HarmonyPatch(
            typeof(MovementComponent),
            nameof(MovementComponent.DeserializeMovementComponent))]
        [HarmonyPostfix]
        internal static void DeserializeMovementComponent_Postfix(
            MovementComponent __instance,
            SerializableWGO.SerializebleMovementComponent data)
        {
            WorldGameObject wgo = __instance?.wgo;
            if (data.state != MovementComponent.MovementState.None ||
                wgo == null || wgo.is_player ||
                wgo.obj_def?.IsNPC() != true)
            {
                return;
            }

            ChunkedGameObject chunk =
                wgo.GetComponent<ChunkedGameObject>();
            if (chunk == null || !chunk.active_now_because_of_movement)
                return;

            chunk.active_now_because_of_movement = false;
            chunk.RecalculateChunk();
            CoopMod.Logger.LogWarning(
                $"[SpawnSync] Repaired stale restored NPC movement lock: " +
                $"obj_id={wgo.obj_id}, tag={wgo.custom_tag}");
        }

        /// <summary>
        /// Several story Flow nodes move an existing NPC directly and then call
        /// OnCameToGDPoint. No WGO is created, so the normal spawn hook cannot see
        /// that transition. Relay client-initiated Flow placements to the host.
        ///
        /// MovementComponent also calls OnCameToGDPoint when a scripted route
        /// finishes. Relay that arrival only for an actor enrolled in the current
        /// locally-authoritative cutscene so the host receives the final semantic
        /// GD point without turning ordinary NPC schedules into client authority.
        /// </summary>
        [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.OnCameToGDPoint))]
        [HarmonyPostfix]
        internal static void OnCameToGDPoint_Postfix(
            WorldGameObject __instance,
            GDPoint p)
        {
            if (__instance == null || p == null ||
                GraveyardKeeperCoop.Multiplayer.SpawnSync.IsApplyingCanonicalFlowPlacement)
            {
                return;
            }

            bool broadcastBishopStock =
                BishopScheduleRepairPatches.ObserveGdPointArrival(
                    __instance,
                    p);
            if (broadcastBishopStock)
            {
                GraveyardKeeperCoop.Multiplayer.SpawnSync.Instance
                    ?.BroadcastCanonicalBishopStockPlacement(
                        __instance,
                        p);
                BishopScheduleRepairPatches.ConfirmStockPlacement(
                    __instance);
                return;
            }

            bool calledFromFlowNode = IsCalledFromFlowNode();
            bool routeCompleted =
                !calledFromFlowNode &&
                (CutsceneSyncPatches.IsAuthoritativeNpcAnimationWindowActive() ||
                 NpcInteractionSyncPatches.IsAuthoritativeNpcAnimationWindowActive()) &&
                GraveyardKeeperCoop.Multiplayer.NpcVisualSync.Instance
                    ?.IsRegisteredLocalCutsceneActorForPathRecovery(__instance) == true;
            if (!calledFromFlowNode && !routeCompleted)
                return;

            GraveyardKeeperCoop.Multiplayer.SpawnSync.Instance
                ?.RequestCanonicalFlowPlacement(
                    __instance,
                    p,
                    routeCompleted);
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

        private static void NotifyCurrentBuildingPreviewChanged()
        {
            GraveyardKeeperCoop.Multiplayer.SpawnSync.Instance
                ?.NotifyBuildingPreviewChanged(
                    FloatingWorldGameObject.cur_floating?.wobj,
                    FloatingWorldGameObject.can_be_built);
        }

        private static string GetInteractionEventsFingerprint(
            WorldGameObject wgo)
        {
            if (wgo?.custom_interaction_events == null ||
                wgo.custom_interaction_events.Count == 0)
            {
                return string.Empty;
            }

            return string.Join("\u001f", wgo.custom_interaction_events.ToArray());
        }

        private static bool IsSyncEnabled()
        {
            var onlineCoop = GraveyardKeeperCoop.Network.OnlineCoopManager.Instance;
            return onlineCoop != null && onlineCoop.IsOnlineCoopEnabled;
        }
    }
}

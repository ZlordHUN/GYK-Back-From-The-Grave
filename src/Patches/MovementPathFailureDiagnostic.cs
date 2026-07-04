using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using GraveyardKeeperCoop.Multiplayer;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Diagnostic logging for A* pathfinding failures. The game already logs
    /// "Failed pathfinding! [obj_id]" but not the intended destination or the
    /// WGO's current position — both of which are needed to diagnose scripted
    /// NPC movement breaking in multiplayer (e.g. the donkey failing to walk
    /// home because its destination is off-navmesh when networked).
    /// </summary>
    [HarmonyPatch(typeof(MovementComponent), "OnPathFailed")]
    public static class MovementPathFailureDiagnostic
    {
        [HarmonyPrefix]
        public static bool Prefix(MovementComponent __instance)
        {
            try
            {
                var wgo = __instance?.wgo;
                if (wgo == null) return true;

                Vector2 dest = __instance.astar != null ? (Vector2)__instance.astar.destination : Vector2.zero;
                if (dest.sqrMagnitude < 0.0001f) return true;

                Vector2 pos = wgo.transform != null ? (Vector2)wgo.transform.position : Vector2.zero;
                float dist = Vector2.Distance(pos, dest);

                bool isDonkey = string.Equals(wgo.obj_id, "donkey", System.StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(wgo.custom_tag, "donkey", System.StringComparison.OrdinalIgnoreCase);

                CoopMod.Logger.LogWarning(
                    $"[PathFail] {(isDonkey ? "DONKEY" : "WGO")} obj_id='{wgo.obj_id}' tag='{wgo.custom_tag}' " +
                    $"pos={pos} dest={dest} dist={dist:F1}" +
                    (isDonkey ? "  << donkey scripted movement failed (destination likely off-navmesh in MP)" : string.Empty));

                var onlineCoop = Network.OnlineCoopManager.Instance;
                bool isAuthoritativeDonkeyExit =
                    isDonkey &&
                    onlineCoop != null &&
                    onlineCoop.IsOnlineCoopEnabled &&
                    onlineCoop.IsHost &&
                    dest.x - pos.x > 1000f &&
                    Mathf.Abs(dest.y - pos.y) < 256f;

                if (isAuthoritativeDonkeyExit)
                {
                    __instance.StopMovement();
                    DonkeyExitRecovery recovery = wgo.GetComponent<DonkeyExitRecovery>();
                    if (recovery == null)
                    {
                        recovery = wgo.gameObject.AddComponent<DonkeyExitRecovery>();
                    }
                    recovery.Initialize(wgo, dest);
                    CoopMod.Logger.LogWarning("[PathFail] Replaced stalled donkey cemetery exit with host-authoritative straight-line recovery");
                    return false;
                }
            }
            catch { }

            return true;
        }
    }

    /// <summary>
    /// The donkey's first cemetery exit targets an off-screen point far outside the
    /// local A* route. If that vanilla route stalls in online co-op, preserve the
    /// visible walk-off and then place the donkey at the intended destination.
    /// </summary>
    internal sealed class DonkeyExitRecovery : MonoBehaviour
    {
        private const float WalkSpeed = 1.2f * 96f;
        private const float VisibleWalkSeconds = 12f;

        private WorldGameObject donkey;
        private Vector2 destination;
        private float startedAt;
        private bool initialized;

        public void Initialize(WorldGameObject targetDonkey, Vector2 targetDestination)
        {
            donkey = targetDonkey;
            destination = targetDestination;
            startedAt = Time.realtimeSinceStartup;
            initialized = donkey != null;

            BaseCharacterComponent character = donkey?.components?.character;
            if (character != null)
            {
                if (character.body != null)
                {
                    character.body.simulated = true;
                    character.body.bodyType = RigidbodyType2D.Kinematic;
                }
                character.LookAt(Direction.Right);
                character.SetAnimationState(CharAnimState.Walking, ItemDefinition.ItemType.None);
            }
            HideDroppedCorpseFromCart();
        }

        private void Update()
        {
            if (!initialized || donkey == null)
            {
                Destroy(this);
                return;
            }

            var onlineCoop = Network.OnlineCoopManager.Instance;
            if (onlineCoop == null || !onlineCoop.IsOnlineCoopEnabled || !onlineCoop.IsHost)
            {
                Destroy(this);
                return;
            }

            Vector3 current = donkey.transform.position;
            Vector2 next2 = Vector2.MoveTowards(
                new Vector2(current.x, current.y),
                destination,
                WalkSpeed * Time.deltaTime);
            Vector3 next = new Vector3(next2.x, next2.y, current.z);

            donkey.transform.position = next;
            BaseCharacterComponent character = donkey.components?.character;
            if (character != null)
            {
                if (character.body != null)
                {
                    character.body.position = next2;
                    character.body.velocity = Vector2.zero;
                    character.body.angularVelocity = 0f;
                }
                character.LookAt(Direction.Right);
                if (character.anim_state != CharAnimState.Walking)
                {
                    character.SetAnimationState(CharAnimState.Walking, ItemDefinition.ItemType.None);
                }
            }
            HideDroppedCorpseFromCart();
            donkey.RefreshPositionCache();
            donkey.round_and_sort?.MarkPositionDirty();

            bool visibleWalkFinished = Time.realtimeSinceStartup - startedAt >= VisibleWalkSeconds;
            bool reachedDestination = (next2 - destination).sqrMagnitude < 1f;
            if (!visibleWalkFinished && !reachedDestination)
            {
                return;
            }

            Vector3 final = new Vector3(destination.x, destination.y, next.z);
            donkey.transform.position = final;
            if (character?.body != null)
            {
                character.body.position = destination;
            }
            donkey.RefreshPositionCache();

            ChunkedGameObject chunk = donkey.GetComponent<ChunkedGameObject>();
            if (chunk != null)
            {
                chunk.active_now_because_of_movement = false;
                chunk.RecalculateChunk();
            }

            CoopMod.Logger.LogInfo($"[DonkeyExit] Walk-off recovery completed at {destination}");
            initialized = false;
            Destroy(this);
        }

        private void LateUpdate()
        {
            if (initialized)
            {
                HideDroppedCorpseFromCart();
            }
        }

        private void HideDroppedCorpseFromCart()
        {
            DonkeyCartCorpseVisualGuard.HideCartCorpseVisuals(donkey);
        }
    }

    /// <summary>
    /// The donkey prefab has direction/animation-specific corpse renderers. Vanilla
    /// hides the active renderer when the body is dropped, but changing to the exit
    /// walk animation can enable another corpse renderer. Latch the first drop and
    /// keep every cart corpse renderer hidden for the remainder of that departure.
    /// </summary>
    internal sealed class DonkeyCartCorpseVisualGuard : MonoBehaviour
    {
        private const float ExitMovementThreshold = 4f;
        private const float NearbyCorpseRendererRadius = 512f;
        private const float CleanupAfterDropSeconds = 30f;
        private const float MaximumLifetimeSeconds = 300f;

        private WorldGameObject donkey;
        private float initialX;
        private float startedAt;
        private float dropObservedAt;
        private float nextNearbyRendererScanAt;
        private bool dropObserved;
        private bool loggedPostSkinMatch;
        private readonly List<SpriteRenderer> trackedCorpseRenderers =
            new List<SpriteRenderer>();

        public static void Ensure(WorldGameObject targetDonkey)
        {
            if (targetDonkey == null)
            {
                return;
            }

            DonkeyCartCorpseVisualGuard guard =
                targetDonkey.GetComponent<DonkeyCartCorpseVisualGuard>();
            if (guard == null)
            {
                guard = targetDonkey.gameObject.AddComponent<DonkeyCartCorpseVisualGuard>();
            }
            guard.Initialize(targetDonkey);
        }

        private void Initialize(WorldGameObject targetDonkey)
        {
            donkey = targetDonkey;
            initialX = donkey.transform.position.x;
            startedAt = Time.realtimeSinceStartup;
            dropObservedAt = 0f;
            nextNearbyRendererScanAt = 0f;
            dropObserved = false;
            loggedPostSkinMatch = false;
            trackedCorpseRenderers.Clear();
        }

        internal void EnforceAfterWgoVisualUpdate()
        {
            if (!dropObserved || donkey == null)
                return;

            int reappliedHostRenderers =
                NpcVisualSync.Instance?.ReapplyLatestHostVisual(donkey) ?? 0;
            int matchedObjects = HideCartCorpseVisuals(
                donkey,
                trackedCorpseRenderers);
            if ((reappliedHostRenderers > 0 || matchedObjects > 0) &&
                !loggedPostSkinMatch)
            {
                loggedPostSkinMatch = true;
                CoopMod.Logger.LogInfo(
                    $"[DonkeyCart] Reasserted corpse-free host cart visual " +
                    $"after local skin update; host_renderers={reappliedHostRenderers}, " +
                    $"fallback_matches={matchedObjects}");
            }
        }

        private void LateUpdate()
        {
            if (donkey == null)
            {
                Destroy(this);
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now - startedAt > MaximumLifetimeSeconds ||
                (dropObserved && now - dropObservedAt > CleanupAfterDropSeconds))
            {
                Destroy(this);
                return;
            }

            TrackCartCorpseRenderers();

            bool donkeyStartedExit =
                donkey.transform.position.x > initialX + ExitMovementThreshold;
            if (!dropObserved && donkeyStartedExit)
            {
                dropObserved = true;
                dropObservedAt = now;
                NpcVisualSync.Instance
                    ?.BroadcastAuthoritativeStateNow(donkey);
                TrackNearbyCartCorpseRenderers();
                int matchedObjects = HideCartCorpseVisuals(
                    donkey,
                    trackedCorpseRenderers);
                CoopMod.Logger.LogInfo(
                    $"[DonkeyCart] Latched dropped corpse visual for donkey departure; " +
                    $"matched_objects={matchedObjects}, tracked_renderers={trackedCorpseRenderers.Count}");
            }

            if (dropObserved)
            {
                if (trackedCorpseRenderers.Count == 0 &&
                    now >= nextNearbyRendererScanAt)
                {
                    TrackNearbyCartCorpseRenderers();
                }
                HideCartCorpseVisuals(donkey, trackedCorpseRenderers);
            }
        }

        private void TrackCartCorpseRenderers()
        {
            SpriteRenderer[] renderers =
                donkey.GetComponentsInChildren<SpriteRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                SpriteRenderer renderer = renderers[i];
                if (renderer != null &&
                    IsExplicitCartCorpseSprite(renderer.sprite?.name) &&
                    !trackedCorpseRenderers.Contains(renderer))
                {
                    trackedCorpseRenderers.Add(renderer);
                }
            }
        }

        private void TrackNearbyCartCorpseRenderers()
        {
            nextNearbyRendererScanAt = Time.realtimeSinceStartup + 0.5f;
            SpriteRenderer[] renderers =
                UnityEngine.Object.FindObjectsOfType<SpriteRenderer>();
            Vector3 donkeyPosition = donkey.transform.position;
            float maxDistanceSqr =
                NearbyCorpseRendererRadius * NearbyCorpseRendererRadius;

            for (int i = 0; i < renderers.Length; i++)
            {
                SpriteRenderer renderer = renderers[i];
                if (renderer == null ||
                    !IsExplicitCartCorpseSprite(renderer.sprite?.name) ||
                    (renderer.transform.position - donkeyPosition).sqrMagnitude >
                    maxDistanceSqr ||
                    trackedCorpseRenderers.Contains(renderer))
                {
                    continue;
                }

                trackedCorpseRenderers.Add(renderer);
            }
        }

        internal static int HideCartCorpseVisuals(
            WorldGameObject targetDonkey,
            List<SpriteRenderer> trackedRenderers = null)
        {
            if (targetDonkey == null)
                return 0;

            int matchedObjects = 0;
            Transform donkeyRoot = targetDonkey.transform;
            Transform[] transforms =
                targetDonkey.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform transform = transforms[i];
                if (transform != donkeyRoot &&
                    IsCartCorpseObject(transform, donkeyRoot))
                {
                    transform.gameObject.SetActive(false);
                    matchedObjects++;
                }
            }

            // The departure corpse can be either a detachable child or baked
            // into the Corpse_express animation set. Replace baked frames with
            // their corpse-free donkey equivalents every LateUpdate so the
            // animator cannot restore the cart corpse.
            SpriteRenderer[] renderers =
                targetDonkey.GetComponentsInChildren<SpriteRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                SpriteRenderer renderer = renderers[i];
                if (TryReplaceCorpseExpressSprite(renderer))
                {
                    matchedObjects++;
                    continue;
                }

                if (renderer != null &&
                    (IsCartCorpseObject(renderer.transform, donkeyRoot) ||
                     IsExplicitCartCorpseSprite(renderer.sprite?.name)))
                {
                    renderer.enabled = false;
                    matchedObjects++;
                }
            }

            if (trackedRenderers != null)
            {
                for (int i = 0; i < trackedRenderers.Count; i++)
                {
                    SpriteRenderer renderer = trackedRenderers[i];
                    if (renderer != null)
                    {
                        renderer.enabled = false;
                    }
                }
            }

            return matchedObjects;
        }

        private static bool TryReplaceCorpseExpressSprite(
            SpriteRenderer renderer)
        {
            string spriteName = renderer?.sprite?.name;
            if (string.IsNullOrEmpty(spriteName))
                return false;

            string replacementName = null;
            if (spriteName.StartsWith(
                    "Corpse_express_body_walk_",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                replacementName =
                    "donkey_body_walk_" +
                    spriteName.Substring(
                        "Corpse_express_body_walk_".Length);
            }
            else if (spriteName.StartsWith(
                         "Corpse_express_head_walk_",
                         System.StringComparison.OrdinalIgnoreCase))
            {
                replacementName =
                    "donkey_head_walk_" +
                    spriteName.Substring(
                        "Corpse_express_head_walk_".Length);
            }
            else if (spriteName.StartsWith(
                         "Corpse_express_trolley_",
                         System.StringComparison.OrdinalIgnoreCase))
            {
                replacementName =
                    "donkey_trolley_" +
                    spriteName.Substring(
                        "Corpse_express_trolley_".Length);
            }

            if (string.IsNullOrEmpty(replacementName))
                return false;

            Sprite replacement =
                EasySpritesCollection.GetSprite(
                    replacementName,
                    false,
                    string.Empty);
            if (replacement == null)
                return false;

            renderer.sprite = replacement;
            return true;
        }

        private static bool IsExplicitCartCorpseSprite(string spriteName)
        {
            return !string.IsNullOrEmpty(spriteName) &&
                   spriteName.IndexOf(
                       "corpse_express_corpse",
                       System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsCartCorpseObject(
            Transform transform,
            Transform donkeyRoot)
        {
            for (Transform current = transform;
                 current != null && current != donkeyRoot;
                 current = current.parent)
            {
                if (current.name.IndexOf(
                        "corpse_express_corpse",
                        System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// WorldGameObject.CustomLateUpdate runs component LateUpdate and then
    /// SkinChanger.CustomLateUpdate. Enforce the host's corpse-free donkey visual
    /// after that entire pipeline; an earlier BaseCharacterComponent hook is
    /// overwritten later in the same frame by the WGO skin changer.
    /// </summary>
    [HarmonyPatch(typeof(WorldGameObject), nameof(WorldGameObject.CustomLateUpdate))]
    internal static class DonkeyCartPostSkinVisualPatch
    {
        [HarmonyPostfix]
        private static void Postfix(WorldGameObject __instance)
        {
            if (__instance == null ||
                (__instance.obj_id != "donkey" &&
                 __instance.custom_tag != "donkey"))
            {
                return;
            }

            __instance.GetComponent<DonkeyCartCorpseVisualGuard>()
                ?.EnforceAfterWgoVisualUpdate();
        }
    }
}

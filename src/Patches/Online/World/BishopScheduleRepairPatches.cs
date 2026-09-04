using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GraveyardKeeperCoop.Multiplayer;
using GraveyardKeeperCoop.Network;
using HarmonyLib;
using UnityEngine;

namespace GraveyardKeeperCoop.Patches
{
    /// <summary>
    /// Repairs the first-burial Bishop when multiplayer timing lets his one-shot
    /// tutorial appearance miss the normal nightly departure. Tutorial provenance
    /// and the next departure boundary are stored on the Bishop so save/reload and
    /// time jumps cannot silently postpone the repair.
    /// </summary>
    [HarmonyPatch]
    internal static class BishopScheduleRepairPatches
    {
        private const string BishopObjId = "npc_bishop";
        private const string TutorialSpawnPoint = "tutor_bishop_spawn_point";
        private const string GraveyardFinishPoint = "gd_finish_bishop";
        private const string DepartureStartPoint = "gd_start_bishop";
        private const string StockPoint = "gd_stock_bishop";
        private const string GoingHomeEvent = "going_home";
        private const string StopWalkingEvent = "stop_walk_and_go_home";
        private const string BackToStartEvent = "on_back_to_start";
        private const string PendingParam =
            "gykmp_tutorial_bishop_departure_pending";
        private const string DepartureAtParam =
            "gykmp_tutorial_bishop_departure_at";
        private const string BishopInGraveyardParam = "bishop_in_graveyard";
        private const string OnTheWayParam = "on_the_way_now";
        private const string LockedParam = "locked";
        private const string MetBishopParam = "met_bishop";
        private const string ChurchLevelParam = "church_level";
        private const string InTutorialParam = "in_tutorial";
        private const float EveningBoundaryFraction = 0.875f;
        private const float MorningBoundaryFraction = 0.375f;
        private const float ScheduledRepairFraction = 0.88f;
        private const float FirstRegularSpawnTime = 6.9f;
        private const float ClockEpsilon = 0.0005f;
        private const float ScanIntervalSeconds = 0.25f;
        private const float LateLegacyScanIntervalSeconds = 2f;
        private const float CandidateDiagnosticIntervalSeconds = 30f;
        private const float NativeSettleSeconds = 0.75f;
        private const float MissingRouteRetrySeconds = 2f;
        private const float RouteStallSeconds = 6f;
        private const float DepartureTimeoutSeconds = 42f;
        private const float ReadinessTimeoutSeconds = 120f;
        private const float MissedArrivalRadius = 16f;
        private const float LegacyFinishRadius = 160f;
        private static readonly Vector2 TutorialFinishPosition =
            new Vector2(1800f, -1450f);
        private static readonly FieldInfo MovementEventField =
            AccessTools.Field(typeof(MovementComponent), "event_on_complete");
        private static readonly FieldInfo MovementTargetPointField =
            AccessTools.Field(typeof(MovementComponent), "_target_gd_point_tag");
        private static readonly Dictionary<int, int> RepairActorGenerations =
            new Dictionary<int, int>();

        private static GameSave observedSave;
        private static WorldGameObject observedBishop;
        private static float nextScanAt;
        private static float nextAttemptAt;
        private static float nextErrorLogAt;
        private static float nextLateLegacyScanAt;
        private static float nextCandidateDiagnosticAt;
        private static float nextWaitDiagnosticAt;
        private static bool repairQueued;
        private static bool ambiguityLogged;
        private static bool driverStartedLogged;
        private static string lastWaitReason;
        private static int sessionGeneration;

        /// <summary>
        /// If the vanilla schedule does reach going_home before our persisted
        /// deadline, make the repair due immediately so it can verify that the
        /// native route completes. This observes only a proven tutorial Bishop
        /// actor at the tutorial finish position and never suppresses the event.
        /// </summary>
        [HarmonyPatch(
            typeof(WorldGameObject),
            nameof(WorldGameObject.FireEvent),
            new Type[] { typeof(string), typeof(float) })]
        [HarmonyPrefix]
        private static void WorldGameObject_FireEvent_Prefix(
            WorldGameObject __instance,
            string event_id)
        {
            if (!string.Equals(
                    event_id,
                    GoingHomeEvent,
                    StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                ObserveVanillaGoingHome(__instance);
            }
            catch (Exception ex)
            {
                if (Time.realtimeSinceStartup < nextErrorLogAt)
                    return;

                nextErrorLogAt = Time.realtimeSinceStartup + 5f;
                CoopMod.Logger.LogWarning(
                    "[BishopScheduleRepair] going_home observation failed: " +
                    ex.Message);
            }
        }

        // Patch Unity's public message directly. MainGame.InGameUpdate is a
        // private helper and can be inlined into Update before Harmony installs
        // its detour, which leaves the original helper body running while its
        // postfix is never reached.
        [HarmonyPatch(typeof(MainGame), nameof(MainGame.Update))]
        [HarmonyPostfix]
        private static void MainGame_Update_Postfix()
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                if (Time.realtimeSinceStartup < nextErrorLogAt)
                    return;

                nextErrorLogAt = Time.realtimeSinceStartup + 5f;
                CoopMod.Logger.LogWarning(
                    $"[BishopScheduleRepair] Tick failed: {ex.Message}");
            }
        }

        internal static bool ObserveGdPointArrival(
            WorldGameObject candidate,
            GDPoint point)
        {
            if (!IsBishop(candidate) || point == null ||
                !IsHostSessionActive())
            {
                return false;
            }

            if (string.Equals(
                    point.gd_tag,
                    TutorialSpawnPoint,
                    StringComparison.Ordinal))
            {
                float target = candidate.GetParam(DepartureAtParam, 0f);
                if (candidate.GetParam(PendingParam, 0f) < 0.5f ||
                    !IsFinite(target) ||
                    target <= 0f)
                {
                    target = CurrentOrNextNightDeparture(
                        MainGame.game_time);
                    candidate.SetParam(PendingParam, 1f);
                    candidate.SetParam(DepartureAtParam, target);
                    CoopMod.Logger.LogInfo(
                        "[BishopScheduleRepair] Marked first-burial Bishop " +
                        $"for departure at game_time={target:F3}");
                }

                observedBishop = candidate;
                return false;
            }

            return string.Equals(
                point.gd_tag,
                StockPoint,
                StringComparison.Ordinal);
        }

        internal static void ConfirmStockPlacement(
            WorldGameObject candidate)
        {
            if (!IsBishop(candidate))
                return;

            bool repaired =
                candidate.GetParam(PendingParam, 0f) >= 0.5f;
            ClearPersistentMarker(candidate);
            RemoveRepairActor(candidate);
            if (ReferenceEquals(observedBishop, candidate))
            {
                observedBishop = null;
            }

            if (repaired)
            {
                CoopMod.Logger.LogInfo(
                    "[BishopScheduleRepair] Tutorial Bishop reached stock; " +
                    "cleared persisted departure repair");
            }
        }

        internal static bool IsRepairDepartureRoute(
            MovementComponent movement,
            WorldGameObject bishop,
            Vector2 destination)
        {
            if (movement == null || !IsBishop(bishop) ||
                !RepairActorGenerations.TryGetValue(
                    bishop.GetInstanceID(),
                    out int generation) ||
                generation != sessionGeneration ||
                float.IsNaN(destination.x) ||
                float.IsInfinity(destination.x) ||
                float.IsNaN(destination.y) ||
                float.IsInfinity(destination.y) ||
                destination.sqrMagnitude < 0.0001f)
            {
                return false;
            }

            return string.Equals(
                       MovementTargetPointField?.GetValue(movement) as string,
                       DepartureStartPoint,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       MovementEventField?.GetValue(movement) as string,
                       BackToStartEvent,
                       StringComparison.Ordinal);
        }

        private static void Tick()
        {
            if (!IsHostSessionActive())
            {
                if (observedSave != null || repairQueued ||
                    RepairActorGenerations.Count > 0)
                {
                    ResetRuntimeState();
                }
                return;
            }

            GameSave save = MainGame.me.save;
            if (!ReferenceEquals(observedSave, save))
            {
                ResetRuntimeState();
                observedSave = save;
            }

            if (!driverStartedLogged)
            {
                driverStartedLogged = true;
                CoopMod.Logger.LogInfo(
                    "[BishopScheduleRepair] MainGame.Update driver active");
            }

            // Discovery and persistent marker arming are safe while the clock
            // or presentation is paused. The coroutine below still requires a
            // mutation-safe world before it normalizes or moves the actor.
            if (TimeOfDay.me == null)
            {
                LogRepairWait(
                    observedBishop,
                    "time-of-day service is not ready",
                    0f);
                return;
            }

            float realtime = Time.realtimeSinceStartup;
            if (realtime < nextScanAt)
                return;
            nextScanAt = realtime + ScanIntervalSeconds;

            WorldGameObject bishop = ResolveMarkedBishop();
            float now = MainGame.game_time;
            if (bishop == null)
                bishop = TryAdoptLegacyTutorialBishop(now);
            if (bishop == null)
                return;

            observedBishop = bishop;
            if (IsAtStock(bishop))
            {
                ConfirmStockPlacement(bishop);
                return;
            }

            float target = bishop.GetParam(DepartureAtParam, 0f);
            if (!IsFinite(target) || target <= 0f)
            {
                target = CurrentOrNextNightDeparture(now);
                bishop.SetParam(DepartureAtParam, target);
            }

            if (IsRegularSpawnBoundary(target))
            {
                // Keep the persisted time clearly outside the boundary matcher
                // so a failed attempt does not re-log this migration every scan.
                target = Mathf.Max(ClockEpsilon, now - 0.01f);
                bishop.SetParam(DepartureAtParam, target);
                CoopMod.Logger.LogWarning(
                    "[BishopScheduleRepair] Existing tutorial repair target " +
                    "collided with a regular Bishop boundary; made it due " +
                    $"immediately at game_time={now:F3}");
            }

            float appropriateTarget =
                CurrentOrNextNightDeparture(now);
            if (target > appropriateTarget + ClockEpsilon)
            {
                float previousTarget = target;
                target = appropriateTarget;
                bishop.SetParam(DepartureAtParam, target);
                CoopMod.Logger.LogInfo(
                    "[BishopScheduleRepair] Advanced a postponed tutorial " +
                    $"departure for the current time phase " +
                    $"(previous_target={previousTarget:F3}, " +
                    $"new_target={target:F3}, game_time={now:F3})");
            }

            if (now + ClockEpsilon < target)
                return;

            if (HasMissedTutorialArrivalIdentity(bishop))
            {
                string waitReason = GetRepairWaitReason(bishop);
                if (!string.IsNullOrEmpty(waitReason))
                {
                    LogRepairWait(bishop, waitReason, now);
                }
            }

            if (repairQueued || realtime < nextAttemptAt)
                return;

            repairQueued = true;
            int generation = sessionGeneration;
            MainGame.me.StartCoroutine(
                RepairDeparture(bishop, bishop.unique_id, generation));
        }

        private static IEnumerator RepairDeparture(
            WorldGameObject initialBishop,
            long uniqueId,
            int generation)
        {
            int actorInstanceId = 0;
            bool completed = false;
            yield return new WaitForSecondsRealtime(NativeSettleSeconds);

            try
            {
                WorldGameObject bishop = initialBishop;
                float readinessStartedAt = Time.realtimeSinceStartup;
                while (IsSameSession(generation))
                {
                    if (Time.realtimeSinceStartup - readinessStartedAt >=
                        ReadinessTimeoutSeconds)
                    {
                        CoopMod.Logger.LogWarning(
                            "[BishopScheduleRepair] Marked Bishop did not " +
                            "reach a repairable departure state; will retry");
                        yield break;
                    }

                    bishop = ResolveRepairBishop(bishop, uniqueId);
                    if (bishop == null)
                    {
                        yield return null;
                        continue;
                    }

                    if (IsAtStock(bishop))
                    {
                        ConfirmStockPlacement(bishop);
                        completed = true;
                        yield break;
                    }

                    if (!CanMutateWorld() ||
                        IsPresentationBusy() ||
                        !NpcInteractionSyncPatches
                            .HasCompletedFirstBishopIntro())
                    {
                        LogRepairWait(
                            bishop,
                            GetRepairWaitReason(bishop),
                            MainGame.game_time);
                        yield return null;
                        continue;
                    }

                    NormalizeMissedTutorialArrival(
                        bishop,
                        "scheduled departure readiness");

                    if (IsRepairReadyAtOrigin(bishop) ||
                        HasNativeDepartureStartedAtOrigin(bishop) ||
                        IsExactDepartureRoute(bishop.components?.character))
                    {
                        break;
                    }

                    yield return null;
                }

                if (!IsSameSession(generation) || bishop == null)
                    yield break;

                bool exactNativeRoute =
                    IsExactDepartureRoute(bishop.components?.character);
                bool nativeDepartureStarted =
                    HasNativeDepartureStartedAtOrigin(bishop);
                if (!exactNativeRoute &&
                    !nativeDepartureStarted &&
                    !IsRepairReadyAtOrigin(bishop))
                {
                    yield break;
                }

                actorInstanceId = bishop.GetInstanceID();
                RepairActorGenerations[actorInstanceId] = generation;

                bool departureAlreadyStarted =
                    nativeDepartureStarted || exactNativeRoute;
                if (!departureAlreadyStarted)
                {
                    bishop.SetParam(OnTheWayParam, 1f);
                    bishop.SetParam(LockedParam, 0f);
                    bishop.FireEvent(GoingHomeEvent);
                    NpcVisualSync.Instance?.BroadcastAuthoritativeStateNow(
                        bishop,
                        reliable: true);
                    CoopMod.Logger.LogWarning(
                        "[BishopScheduleRepair] Started missed tutorial " +
                        "departure through Bishop's native going_home event");
                    yield return new WaitForSecondsRealtime(
                        NativeSettleSeconds);
                }

                if (IsAtStock(bishop))
                {
                    ConfirmStockPlacement(bishop);
                    completed = true;
                    yield break;
                }

                if (!IsExactDepartureRoute(bishop.components?.character))
                {
                    bishop.FireEvent(StopWalkingEvent);
                    CoopMod.Logger.LogWarning(
                        "[BishopScheduleRepair] Native departure did not " +
                        "start its exit route; fired stop_walk_and_go_home");
                }

                float startedAt = Time.realtimeSinceStartup;
                float routeMissingSince = startedAt;
                float lastProgressAt = startedAt;
                float findingStartedAt = -1f;
                Vector2 lastPosition = bishop.transform.position;
                bool routeRetried = false;

                while (IsSameSession(generation) &&
                       Time.realtimeSinceStartup - startedAt <
                       DepartureTimeoutSeconds)
                {
                    bishop = ResolveRepairBishop(bishop, uniqueId);
                    if (bishop == null)
                    {
                        yield return null;
                        continue;
                    }

                    if (IsAtStock(bishop))
                    {
                        ConfirmStockPlacement(bishop);
                        completed = true;
                        yield break;
                    }

                    MovementComponent movement =
                        bishop.components?.character;
                    bool exactRoute = IsExactDepartureRoute(movement);
                    bool finding = movement?.astar?.finding == true;
                    bool recovering =
                        bishop.GetComponent<CutsceneNpcPathRecovery>()
                            ?.IsActive == true;
                    Vector2 position = bishop.transform.position;
                    if ((position - lastPosition).sqrMagnitude >= 16f)
                    {
                        lastPosition = position;
                        lastProgressAt = Time.realtimeSinceStartup;
                    }
                    if (recovering)
                        lastProgressAt = Time.realtimeSinceStartup;
                    if (finding)
                    {
                        if (findingStartedAt < 0f)
                            findingStartedAt = Time.realtimeSinceStartup;
                    }
                    else
                    {
                        findingStartedAt = -1f;
                    }

                    if (exactRoute)
                    {
                        routeMissingSince = Time.realtimeSinceStartup;
                    }
                    else if (!routeRetried &&
                             Time.realtimeSinceStartup - routeMissingSince >=
                             MissingRouteRetrySeconds)
                    {
                        routeRetried = true;
                        bishop.FireEvent(StopWalkingEvent);
                        CoopMod.Logger.LogWarning(
                            "[BishopScheduleRepair] Retried missing Bishop " +
                            "departure route once");
                        routeMissingSince = Time.realtimeSinceStartup;
                    }

                    bool routeStalled =
                        exactRoute &&
                        !finding &&
                        !recovering &&
                        Time.realtimeSinceStartup - lastProgressAt >=
                            RouteStallSeconds;
                    bool pathSearchStalled =
                        exactRoute &&
                        finding &&
                        findingStartedAt >= 0f &&
                        Time.realtimeSinceStartup - findingStartedAt >=
                            RouteStallSeconds;
                    routeStalled = routeStalled || pathSearchStalled;
                    if (routeStalled && routeRetried)
                        break;
                    if (routeStalled)
                    {
                        routeRetried = true;
                        bishop.FireEvent(StopWalkingEvent);
                        lastProgressAt = Time.realtimeSinceStartup;
                        CoopMod.Logger.LogWarning(
                            "[BishopScheduleRepair] Retried stalled Bishop " +
                            "departure route once");
                    }

                    yield return null;
                }

                if (!IsSameSession(generation) || bishop == null)
                    yield break;

                CutsceneNpcPathRecovery recovery =
                    bishop.GetComponent<CutsceneNpcPathRecovery>();
                recovery?.Cancel();
                bishop.components?.character?.StopMovement();
                bishop.FireEvent(BackToStartEvent);
                CoopMod.Logger.LogWarning(
                    "[BishopScheduleRepair] Bishop exit did not finish in " +
                    "time; invoked native on_back_to_start fallback");
                yield return new WaitForSecondsRealtime(1f);

                if (!IsAtStock(bishop))
                {
                    completed = PlaceAtStock(bishop);
                    if (completed)
                    {
                        ConfirmStockPlacement(bishop);
                        CoopMod.Logger.LogWarning(
                            "[BishopScheduleRepair] Native stock event did " +
                            "not complete; applied semantic stock fallback");
                    }
                }
                else
                {
                    ConfirmStockPlacement(bishop);
                    completed = true;
                }
            }
            finally
            {
                if (actorInstanceId != 0 &&
                    RepairActorGenerations.TryGetValue(
                        actorInstanceId,
                        out int actorGeneration) &&
                    actorGeneration == generation)
                {
                    RepairActorGenerations.Remove(actorInstanceId);
                }

                if (generation == sessionGeneration)
                {
                    repairQueued = false;
                    if (!completed)
                        nextAttemptAt = Time.realtimeSinceStartup + 5f;
                }
            }
        }

        private static bool PlaceAtStock(WorldGameObject bishop)
        {
            if (!IsBishop(bishop) || bishop.is_removed)
                return false;

            GDPoint stock =
                WorldMap.GetGDPointByGDTag(StockPoint, true, true);
            if (stock == null)
            {
                CoopMod.Logger.LogError(
                    "[BishopScheduleRepair] Could not resolve gd_stock_bishop");
                return false;
            }

            MovementComponent movement = bishop.components?.character;
            movement?.StopMovement();
            bishop.SetParam(BishopInGraveyardParam, 0f);
            bishop.SetParam(OnTheWayParam, 1f);
            bishop.SetParam(LockedParam, 0f);
            bishop.transform.position = stock.transform.position;
            bishop.RefreshPositionCache();
            bishop.OnCameToGDPoint(stock);

            ChunkedGameObject chunk =
                bishop.GetComponent<ChunkedGameObject>();
            if (chunk != null)
            {
                chunk.active_now_because_of_movement = false;
                chunk.RecalculateChunk();
            }

            NpcVisualSync.Instance?.BroadcastAuthoritativeStateNow(
                bishop,
                reliable: true);
            return IsAtStock(bishop);
        }

        private static WorldGameObject ResolveMarkedBishop()
        {
            List<WorldGameObject> bishops = GetBishopCandidates();
            WorldGameObject marked = null;
            int count = 0;
            for (int i = 0; i < bishops.Count; i++)
            {
                WorldGameObject candidate = bishops[i];
                if (candidate.GetParam(PendingParam, 0f) < 0.5f)
                    continue;
                marked = candidate;
                count++;
            }

            if (count == 1)
            {
                ambiguityLogged = false;
                return marked;
            }

            if (count > 1 && !ambiguityLogged)
            {
                ambiguityLogged = true;
                CoopMod.Logger.LogError(
                    "[BishopScheduleRepair] Multiple marked Bishop actors " +
                    "found; aborting repair instead of choosing one");
            }
            return null;
        }

        private static WorldGameObject TryAdoptLegacyTutorialBishop(
            float now)
        {
            bool allowCanonicalArrival =
                now + ClockEpsilon < FirstRegularSpawnTime;
            float realtime = Time.realtimeSinceStartup;
            if (!allowCanonicalArrival &&
                realtime < nextLateLegacyScanAt)
            {
                return null;
            }
            if (!allowCanonicalArrival)
            {
                nextLateLegacyScanAt =
                    realtime + LateLegacyScanIntervalSeconds;
            }

            List<WorldGameObject> bishops = GetBishopCandidates();
            WorldGameObject exactMissedArrival = null;
            int exactMissedArrivalCount = 0;
            bool hasFinishPositionCandidate = false;
            for (int i = 0; i < bishops.Count; i++)
            {
                if (IsAtTutorialFinishPosition(bishops[i]))
                    hasFinishPositionCandidate = true;
                if (HasMissedTutorialArrivalIdentity(bishops[i]))
                {
                    exactMissedArrival = bishops[i];
                    exactMissedArrivalCount++;
                }
            }

            if (exactMissedArrivalCount > 1)
            {
                if (!ambiguityLogged)
                {
                    ambiguityLogged = true;
                    CoopMod.Logger.LogError(
                        "[BishopScheduleRepair] Multiple exact stuck " +
                        "tutorial Bishop actors found; repair was not armed");
                }
                return null;
            }

            if (exactMissedArrivalCount == 1)
            {
                // This save-only identity cannot be produced by a normal weekly
                // visit. Persist authority immediately, even if a GUI, cutscene,
                // disabled clock, or transient movement state makes it unsafe to
                // complete OnCameToGDPoint this frame.
                float exactTarget = CurrentOrNextNightDeparture(now);
                exactMissedArrival.SetParam(PendingParam, 1f);
                exactMissedArrival.SetParam(DepartureAtParam, exactTarget);
                observedBishop = exactMissedArrival;
                ambiguityLogged = false;

                bool normalized = false;
                if (CanMutateWorld() &&
                    !IsPresentationBusy() &&
                    NpcInteractionSyncPatches
                        .HasCompletedFirstBishopIntro())
                {
                    normalized = NormalizeMissedTutorialArrival(
                        exactMissedArrival,
                        "exact tutorial-state adoption");
                }

                CoopMod.Logger.LogWarning(
                    "[BishopScheduleRepair] Armed proven stuck tutorial " +
                    $"Bishop uid={exactMissedArrival.unique_id}; " +
                    $"departure_at={exactTarget:F3}, game_time={now:F3}, " +
                    $"normalized_now={normalized}");

                if (!normalized)
                {
                    LogRepairWait(
                        exactMissedArrival,
                        GetRepairWaitReason(exactMissedArrival),
                        now);
                }
                return exactMissedArrival;
            }

            // A canonical finish-point Bishop becomes ambiguous once weekly
            // visits begin. The spawn-tag/finish-position/bgrave=0 state cannot
            // be produced by a normal visit, so that exact migration remains
            // safe no matter how late the affected save is loaded.
            if (!allowCanonicalArrival)
                return null;
            if (!hasFinishPositionCandidate)
                return null;

            WorldGameObject eligible = null;
            int count = 0;
            for (int i = 0; i < bishops.Count; i++)
            {
                WorldGameObject candidate = bishops[i];
                if (!IsLegacyTutorialCandidate(
                        candidate,
                        allowCanonicalArrival))
                    continue;
                eligible = candidate;
                count++;
            }

            if (count != 1)
            {
                if (count > 1 && !ambiguityLogged)
                {
                    ambiguityLogged = true;
                    CoopMod.Logger.LogError(
                        "[BishopScheduleRepair] Multiple legacy tutorial " +
                        "Bishop candidates found; repair was not armed");
                }
                LogLegacyCandidateDiagnostics(bishops, now);
                return null;
            }

            if (!NormalizeMissedTutorialArrival(
                    eligible,
                    "legacy tutorial adoption"))
            {
                LogLegacyCandidateDiagnostics(bishops, now);
                return null;
            }

            float target = CurrentOrNextNightDeparture(now);
            eligible.SetParam(PendingParam, 1f);
            eligible.SetParam(DepartureAtParam, target);
            ambiguityLogged = false;
            CoopMod.Logger.LogWarning(
                "[BishopScheduleRepair] Adopted proven stuck tutorial " +
                $"Bishop; departure scheduled for game_time={target:F3}");
            return eligible;
        }

        private static bool IsLegacyTutorialCandidate(
            WorldGameObject bishop,
            bool allowCanonicalArrival)
        {
            WorldGameObject player = MainGame.me?.player;
            MovementComponent movement = bishop?.components?.character;
            bool missedArrival = IsMissedTutorialArrival(bishop);
            bool canonicalArrival =
                allowCanonicalArrival &&
                IsCanonicalTutorialArrival(bishop);
            bool canonicalTutorialProof =
                player != null &&
                player.GetParam(MetBishopParam, 0f) >= 1f &&
                player.GetParam(ChurchLevelParam, 0f) >= 1f &&
                player.GetParam(InTutorialParam, 0f) < 0.5f;
            return IsBishop(bishop) &&
                   !bishop.is_removed &&
                   (missedArrival ||
                    (canonicalArrival && canonicalTutorialProof)) &&
                   bishop.GetParam(OnTheWayParam, 0f) < 0.5f &&
                   movement != null &&
                   movement.movement_state ==
                       MovementComponent.MovementState.None &&
                   movement.astar?.finding != true &&
                   bishop.GetComponent<CutsceneNpcPathRecovery>()
                       ?.IsActive != true &&
                   NpcInteractionSyncPatches
                       .HasCompletedFirstBishopIntro() &&
                   !IsPresentationBusy();
        }

        /// <summary>
        /// The affected saves contain a very specific half-completed arrival:
        /// the transform is already at gd_finish_bishop, but cur_gd_point still
        /// names tutor_bishop_spawn_point and bishop_in_graveyard was never set.
        /// Complete only those missing arrival semantics. Do not fire
        /// on_came_to_finish, because that would replay the prologue intro.
        /// </summary>
        private static bool NormalizeMissedTutorialArrival(
            WorldGameObject bishop,
            string source)
        {
            if (IsCanonicalTutorialArrival(bishop))
                return true;
            if (!IsMissedTutorialArrival(bishop) ||
                !CanMutateWorld() ||
                IsPresentationBusy())
            {
                return false;
            }

            GDPoint finish =
                WorldMap.GetGDPointByGDTag(
                    GraveyardFinishPoint,
                    true,
                    true);
            if (finish == null)
            {
                CoopMod.Logger.LogError(
                    "[BishopScheduleRepair] Could not normalize missed " +
                    "tutorial arrival: gd_finish_bishop was not found");
                return false;
            }

            string previousPoint = bishop.cur_gd_point;
            float previousInGraveyard =
                bishop.GetParam(BishopInGraveyardParam, 0f);
            bishop.OnCameToGDPoint(finish);
            bishop.SetParam(BishopInGraveyardParam, 1f);
            bishop.SetParam(OnTheWayParam, 0f);

            bool normalized = IsCanonicalTutorialArrival(bishop);
            if (normalized)
            {
                CoopMod.Logger.LogWarning(
                    "[BishopScheduleRepair] Normalized missed first-Bishop " +
                    $"arrival ({source}; uid={bishop.unique_id}, " +
                    $"previous_point={previousPoint ?? "<null>"}, " +
                    $"previous_bgrave={previousInGraveyard:F0}); " +
                    "intro event was not replayed");
            }
            else
            {
                CoopMod.Logger.LogError(
                    "[BishopScheduleRepair] Bishop arrival normalization " +
                    $"did not stick ({source}; uid={bishop.unique_id})");
            }
            return normalized;
        }

        private static bool IsCanonicalTutorialArrival(
            WorldGameObject bishop)
        {
            return IsBishop(bishop) &&
                   !bishop.is_removed &&
                   string.Equals(
                       bishop.cur_gd_point,
                       GraveyardFinishPoint,
                       StringComparison.Ordinal) &&
                   IsAtTutorialFinishPosition(bishop) &&
                   bishop.GetParam(BishopInGraveyardParam, 0f) >= 0.5f;
        }

        private static bool IsMissedTutorialArrival(
            WorldGameObject bishop)
        {
            MovementComponent movement = bishop?.components?.character;
            return HasMissedTutorialArrivalIdentity(bishop) &&
                   bishop.GetParam(OnTheWayParam, 0f) < 0.5f &&
                   movement != null &&
                   movement.movement_state ==
                       MovementComponent.MovementState.None &&
                   movement.astar?.finding != true &&
                   bishop.GetComponent<CutsceneNpcPathRecovery>()
                       ?.IsActive != true;
        }

        private static bool HasMissedTutorialArrivalIdentity(
            WorldGameObject bishop)
        {
            return IsBishop(bishop) &&
                   !bishop.is_removed &&
                   string.Equals(
                       bishop.cur_gd_point,
                       TutorialSpawnPoint,
                       StringComparison.Ordinal) &&
                   IsAtExactTutorialFinishPosition(bishop) &&
                   bishop.GetParam(BishopInGraveyardParam, 0f) < 0.5f;
        }

        private static bool IsAtExactTutorialFinishPosition(
            WorldGameObject bishop)
        {
            return bishop != null &&
                   Vector2.Distance(
                       bishop.transform.position,
                       TutorialFinishPosition) <= MissedArrivalRadius;
        }

        private static bool IsAtTutorialFinishPosition(
            WorldGameObject bishop)
        {
            return bishop != null &&
                   Vector2.Distance(
                       bishop.transform.position,
                       TutorialFinishPosition) <= LegacyFinishRadius;
        }

        private static void ObserveVanillaGoingHome(
            WorldGameObject bishop)
        {
            if (!IsHostSessionActive() ||
                !IsBishop(bishop) ||
                bishop.is_removed ||
                !IsAtDepartureOrigin(bishop) ||
                !NpcInteractionSyncPatches.HasCompletedFirstBishopIntro())
            {
                return;
            }

            float now = MainGame.game_time;
            bool exactMissedArrival =
                HasMissedTutorialArrivalIdentity(bishop);
            if (!IsFinite(now) ||
                (now + ClockEpsilon >= FirstRegularSpawnTime &&
                 !exactMissedArrival))
            {
                return;
            }

            float previousTarget =
                bishop.GetParam(DepartureAtParam, 0f);
            bool alreadyDue =
                bishop.GetParam(PendingParam, 0f) >= 0.5f &&
                IsFinite(previousTarget) &&
                previousTarget > 0f &&
                previousTarget <= now + ClockEpsilon;
            if (alreadyDue)
                return;

            bool normalized =
                NormalizeMissedTutorialArrival(
                    bishop,
                    "vanilla going_home event");
            if (!normalized &&
                HasMissedTutorialArrivalIdentity(bishop))
            {
                // The actor can be between movement states while vanilla starts
                // its route. Persist the repair now and normalize at readiness.
            }
            else if (!normalized &&
                     !IsCanonicalTutorialArrival(bishop))
            {
                return;
            }

            bishop.SetParam(PendingParam, 1f);
            bishop.SetParam(DepartureAtParam, now);
            observedBishop = bishop;
            nextScanAt = 0f;
            nextAttemptAt = 0f;
            CoopMod.Logger.LogInfo(
                "[BishopScheduleRepair] Observed vanilla going_home for " +
                $"the tutorial Bishop; persisted repair is due now " +
                $"(game_time={now:F3}, previous_target={previousTarget:F3})");
        }

        private static void LogLegacyCandidateDiagnostics(
            List<WorldGameObject> bishops,
            float now)
        {
            float realtime = Time.realtimeSinceStartup;
            if (realtime < nextCandidateDiagnosticAt)
                return;
            nextCandidateDiagnosticAt =
                realtime + CandidateDiagnosticIntervalSeconds;

            WorldGameObject player = MainGame.me?.player;
            bool introComplete =
                NpcInteractionSyncPatches.HasCompletedFirstBishopIntro();
            bool presentationBusy = IsPresentationBusy();
            if (bishops == null || bishops.Count == 0)
            {
                CoopMod.Logger.LogWarning(
                    "[BishopScheduleRepair] Legacy scan found no live " +
                    $"npc_bishop actor (game_time={now:F3}, " +
                    $"intro_complete={introComplete}, " +
                    $"presentation_busy={presentationBusy})");
                return;
            }

            for (int i = 0; i < bishops.Count; i++)
            {
                WorldGameObject bishop = bishops[i];
                if (!IsAtTutorialFinishPosition(bishop))
                    continue;
                MovementComponent movement = bishop.components?.character;
                Vector2 position = bishop.transform.position;
                string movementState = movement == null
                    ? "<none>"
                    : movement.movement_state.ToString();
                CoopMod.Logger.LogWarning(
                    "[BishopScheduleRepair] Rejected legacy Bishop " +
                    $"candidate uid={bishop.unique_id}: " +
                    $"pos=({position.x:F1},{position.y:F1}), " +
                    $"gd={bishop.cur_gd_point ?? "<null>"}, " +
                    $"bgrave={bishop.GetParam(BishopInGraveyardParam, 0f):F0}, " +
                    $"on_way={bishop.GetParam(OnTheWayParam, 0f):F0}, " +
                    $"movement={movementState}, " +
                    $"finding={movement?.astar?.finding == true}, " +
                    $"recovery={bishop.GetComponent<CutsceneNpcPathRecovery>()?.IsActive == true}, " +
                    $"met={player?.GetParam(MetBishopParam, 0f) ?? 0f:F0}, " +
                    $"church={player?.GetParam(ChurchLevelParam, 0f) ?? 0f:F0}, " +
                    $"tutorial={player?.GetParam(InTutorialParam, 0f) ?? 0f:F0}, " +
                    $"intro_complete={introComplete}, " +
                    $"presentation_busy={presentationBusy}");
            }
        }

        private static List<WorldGameObject> GetBishopCandidates()
        {
            var result = new List<WorldGameObject>();
            List<WorldGameObject> matches =
                WorldMap.GetWorldGameObjectsByObjId(BishopObjId);
            if (matches == null)
                return result;

            for (int i = 0; i < matches.Count; i++)
            {
                WorldGameObject candidate = matches[i];
                if (IsBishop(candidate) && !candidate.is_removed)
                    result.Add(candidate);
            }
            return result;
        }

        private static WorldGameObject ResolveRepairBishop(
            WorldGameObject candidate,
            long uniqueId)
        {
            if (IsBishop(candidate) && !candidate.is_removed &&
                (uniqueId <= 0L || candidate.unique_id == uniqueId))
            {
                return candidate;
            }

            if (uniqueId > 0L)
            {
                WorldGameObject byId =
                    WorldMap.GetWorldGameObjectByUniqueId(uniqueId, true);
                if (IsBishop(byId) && !byId.is_removed)
                    return byId;
            }

            WorldGameObject marked = ResolveMarkedBishop();
            return IsBishop(marked) ? marked : null;
        }

        private static bool IsExactDepartureRoute(
            MovementComponent movement)
        {
            return movement != null &&
                   string.Equals(
                       MovementTargetPointField?.GetValue(movement) as string,
                       DepartureStartPoint,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       MovementEventField?.GetValue(movement) as string,
                       BackToStartEvent,
                       StringComparison.Ordinal);
        }

        private static bool IsAtDepartureOrigin(
            WorldGameObject bishop)
        {
            return IsBishop(bishop) &&
                   ((string.Equals(
                        bishop.cur_gd_point,
                        GraveyardFinishPoint,
                        StringComparison.Ordinal) &&
                     IsAtTutorialFinishPosition(bishop)) ||
                    (string.Equals(
                        bishop.cur_gd_point,
                        TutorialSpawnPoint,
                        StringComparison.Ordinal) &&
                     IsAtExactTutorialFinishPosition(bishop)));
        }

        private static bool IsRepairReadyAtOrigin(
            WorldGameObject bishop)
        {
            MovementComponent movement = bishop?.components?.character;
            return IsAtDepartureOrigin(bishop) &&
                   bishop.GetParam(BishopInGraveyardParam, 0f) >= 0.5f &&
                   bishop.GetParam(OnTheWayParam, 0f) < 0.5f &&
                   movement != null &&
                   movement.movement_state ==
                       MovementComponent.MovementState.None &&
                   movement.astar?.finding != true &&
                   bishop.GetComponent<CutsceneNpcPathRecovery>()
                       ?.IsActive != true;
        }

        private static bool HasNativeDepartureStartedAtOrigin(
            WorldGameObject bishop)
        {
            return IsAtDepartureOrigin(bishop) &&
                   (bishop.GetParam(OnTheWayParam, 0f) >= 0.5f ||
                    (!HasMissedTutorialArrivalIdentity(bishop) &&
                     bishop.GetParam(BishopInGraveyardParam, 0f) < 0.5f));
        }

        private static bool IsAtStock(WorldGameObject bishop)
        {
            return IsBishop(bishop) &&
                   string.Equals(
                       bishop.cur_gd_point,
                       StockPoint,
                       StringComparison.Ordinal);
        }

        private static bool IsBishop(WorldGameObject candidate)
        {
            return candidate != null &&
                   string.Equals(
                       candidate.obj_id,
                       BishopObjId,
                       StringComparison.Ordinal);
        }

        private static bool IsHostSessionActive()
        {
            OnlineCoopManager coop = OnlineCoopManager.Instance;
            return coop != null &&
                   coop.IsOnlineCoopEnabled &&
                   coop.IsHost &&
                   MainGame.game_started &&
                   MainGame.me != null &&
                   MainGame.me.save != null &&
                   MainGame.me.player != null;
        }

        private static bool IsSameSession(int generation)
        {
            return generation == sessionGeneration &&
                   IsHostSessionActive() &&
                   ReferenceEquals(observedSave, MainGame.me.save);
        }

        private static bool CanMutateWorld()
        {
            return !MainGame.paused &&
                   EnvironmentEngine.me != null &&
                   !EnvironmentEngine.me.IsTimeStopped();
        }

        private static string GetRepairWaitReason(WorldGameObject bishop)
        {
            if (MainGame.paused)
                return "the game is paused";
            if (EnvironmentEngine.me == null)
                return "the environment service is not ready";
            if (EnvironmentEngine.me.IsTimeStopped())
                return "the game clock or local control is disabled";
            if (IsPresentationBusy())
                return "dialogue or cutscene presentation is active";
            if (!NpcInteractionSyncPatches.HasCompletedFirstBishopIntro())
                return "durable first-Bishop intro proof is incomplete";

            MovementComponent movement = bishop?.components?.character;
            if (movement == null)
                return "the Bishop movement component is not ready";
            if (movement.astar?.finding == true)
                return "the Bishop is still resolving a path";
            if (movement.movement_state !=
                MovementComponent.MovementState.None)
            {
                return "the Bishop movement state is " +
                       movement.movement_state;
            }
            if (bishop?.GetComponent<CutsceneNpcPathRecovery>()
                    ?.IsActive == true)
            {
                return "Bishop path recovery is active";
            }
            return null;
        }

        private static void LogRepairWait(
            WorldGameObject bishop,
            string reason,
            float now)
        {
            if (string.IsNullOrEmpty(reason))
                return;

            float realtime = Time.realtimeSinceStartup;
            if (string.Equals(
                    lastWaitReason,
                    reason,
                    StringComparison.Ordinal) &&
                realtime < nextWaitDiagnosticAt)
            {
                return;
            }

            lastWaitReason = reason;
            nextWaitDiagnosticAt = realtime +
                                   CandidateDiagnosticIntervalSeconds;
            string actor = bishop == null
                ? "unresolved"
                : bishop.unique_id.ToString();
            CoopMod.Logger.LogWarning(
                "[BishopScheduleRepair] Repair waiting: " +
                $"{reason} (uid={actor}, game_time={now:F3})");
        }

        private static bool IsPresentationBusy()
        {
            return LocalCoopDialoguePatches.IsAnyPlayerInDialogue() ||
                   DialogueSync.Instance?.IsInSyncedDialogue == true ||
                   CutsceneSyncPatches.IsApplyingRemotePresentation ||
                   CutsceneSyncPatches
                       .IsAuthoritativeNpcAnimationWindowActive() ||
                   CutsceneSyncPatches
                       .IsNpcNetworkPuppetWindowActive() ||
                   NpcInteractionSyncPatches
                       .IsAuthoritativeNpcAnimationWindowActive() ||
                   NpcInteractionSyncPatches
                       .IsNpcNetworkPuppetWindowActive();
        }

        private static float CurrentOrNextNightDeparture(float now)
        {
            float day = Mathf.Floor(now);
            float fraction = now - day;
            bool activeNight =
                fraction + ClockEpsilon >= EveningBoundaryFraction ||
                fraction < MorningBoundaryFraction;
            if (activeNight)
                return Mathf.Max(now, ClockEpsilon);

            // Arm just after evening_came (.875) so this proven tutorial actor
            // departs before a weekly Bishop spawn at n+.9 can collide with it.
            return day + ScheduledRepairFraction;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsRegularSpawnBoundary(float boundary)
        {
            if (boundary + ClockEpsilon < FirstRegularSpawnTime)
                return false;

            float cycle =
                (boundary - FirstRegularSpawnTime) / 6f;
            return Mathf.Abs(cycle - Mathf.Round(cycle)) <= 0.001f;
        }

        private static void ClearPersistentMarker(
            WorldGameObject bishop)
        {
            bishop.SetParam(PendingParam, 0f);
            bishop.SetParam(DepartureAtParam, 0f);
        }

        private static void RemoveRepairActor(
            WorldGameObject bishop)
        {
            if (bishop != null)
                RepairActorGenerations.Remove(bishop.GetInstanceID());
        }

        private static void ResetRuntimeState()
        {
            sessionGeneration++;
            observedSave = null;
            observedBishop = null;
            nextScanAt = 0f;
            nextAttemptAt = 0f;
            nextLateLegacyScanAt = 0f;
            nextCandidateDiagnosticAt = 0f;
            nextWaitDiagnosticAt = 0f;
            repairQueued = false;
            ambiguityLogged = false;
            driverStartedLogged = false;
            lastWaitReason = null;
            RepairActorGenerations.Clear();
        }
    }
}

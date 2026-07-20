using ASWDEBUG.Logger;
using ASWDEBUG.Cheats.SurvivalBot;
using RAIN.Navigation;
using RAIN.Navigation.Graph;
using RAIN.Navigation.Pathfinding;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace ASWDEBUG.Cheats.AutoBattle
{
    internal sealed class AutoBattleRouteResult
    {
        public bool Success;
        public bool Partial;
        public string Provider;
        public string Detail;
        public readonly List<Vector3> Corners = new List<Vector3>(48);
        public readonly List<bool> JumpFlags = new List<bool>(48);
    }

    internal sealed class AutoBattleRouteCapabilities
    {
        public float JumpHeight = 1.2f;
        public float JumpVelocity = 6.5f;
        public float RunSpeed = 6.0f;
        public bool AllowJump = true;
        public bool RequireRainPath;
    }

    internal enum AutoBattleNavResourceState
    {
        Unavailable,
        Loading,
        Ready,
        Fallback
    }

    internal static class AutoBattleRoutePlanner
    {
        private const float CellSize = 1.0f;
        private const float HeightLayerSize = 0.50f;
        private const int MaxNodes = 14000;
        private const int MaxNodesPerSlice = 1536;
        private const int MinSearchSliceMilliseconds = 7;
        private const int MaxSearchSliceMilliseconds = 20;
        private const float TargetFrameMilliseconds = 20.0f;
        private const float MaxRouteRadius = 96f;
        private static float _nextRouteLogTime;
        private const float GoalTolerance = 1.0f;
        private const float GroundRayUp = 4.0f;
        private const float GroundRayDown = 10.0f;
        private const float MaxStepHeight = 1.25f;
        private const float NavLoadTimeout = 8.0f;
        private const float MaxWalkableRampGrade = 0.92f;
        private const float RainCornerClearanceRadius = 0.62f;
        private const float NavigationBodyRadius = 0.48f;
        private const float RainShortcutCorridorRadius = 0.28f;

        private static readonly int[] Dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] Dz = { 0, 0, 1, -1, 1, -1, 1, -1 };
        private static readonly float[] MoveCost = { 1f, 1f, 1f, 1f, 1.4142f, 1.4142f, 1.4142f, 1.4142f };
        private static readonly float[] WalkProbeHeights = { 0.35f, 0.9f, 1.45f };
        private static readonly float[] JumpProbeHeights = { 0.32f, 1.12f };
        private static readonly float[] NavigationProbeHeights = { 0.38f, 0.92f, 1.42f };
        private static readonly float[] NavigationProbeOffsetScales = { -1f, 0f, 1f };

        private static int _groundMask = int.MinValue;
        private static int _blockMask = int.MinValue;

        private static string _manifestText;
        private static bool _manifestRead;
        private static string _navMapName = string.Empty;
        private static bool _navResourceDeclared;
        private static bool _navLoadRequested;
        private static float _navLoadStartedAt;
        private static float _nextNavProbeTime;
        private static float _nextNavLogTime;
        private static AutoBattleNavResourceState _navState = AutoBattleNavResourceState.Unavailable;
        private static PhysicsSearchJob _physicsSearchJob;
        private static RainSearchJob _rainSearchJob;
        private static int _sliceBudgetFrame = -1;
        private static float _sliceBudgetMilliseconds;
        private static float _sliceSpentMilliseconds;
        private static float _frameMillisecondsEma = TargetFrameMilliseconds;
        private static string _mapBakeDeferredReason = string.Empty;

        internal static AutoBattleNavResourceState NavigationState
        {
            get { return _navState; }
        }

        internal static bool IsGameNavigationReady
        {
            get { return _navState == AutoBattleNavResourceState.Ready; }
        }

        internal static bool IsPointOnOwnedRainGraph(Vector3 point, float tolerance)
        {
            try
            {
                NavigationManager manager = NavigationManager.Instance;
                RAINNavigationGraph ownedGraph = RuntimeRainNavMesh.OwnedGraph;
                if (manager == null || ownedGraph == null) return false;
                List<RAINNavigationGraph> graphs = manager.GraphsForPoints(
                    point, point, Mathf.Max(0.25f, tolerance), NavigationManager.GraphType.Navmesh, null);
                return graphs != null && graphs.Contains(ownedGraph);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsSafeRainNavigationAnchor(Vector3 point, Transform ignoreRoot)
        {
            return IsPointOnOwnedRainGraph(point, 0.85f) &&
                   HasStandingSpace(point, ignoreRoot) &&
                   MeasureWallClearance(point, ignoreRoot) >= NavigationBodyRadius + 0.12f;
        }

        internal static void TickNavigation(Level level, Character player, bool navigationActive)
        {
            RuntimeRainNavMesh.Tick(level, player, navigationActive);
            if (player != null && player.transform != null)
                UpdateNavigationStatus(player.transform.position);
        }

        internal static void ShutdownNavigation(string reason)
        {
            _physicsSearchJob = null;
            _rainSearchJob = null;
            RuntimeRainNavMesh.Shutdown(reason);
            ResetNavigationState();
        }

        internal static void DeactivateNavigation(string reason)
        {
            DeactivateNavigation(reason, false);
        }

        internal static void DeactivateNavigationForSceneExit(string reason)
        {
            DeactivateNavigation(reason, true);
        }

        private static void DeactivateNavigation(string reason, bool releaseMemoryCache)
        {
            _physicsSearchJob = null;
            _rainSearchJob = null;
            if (SurvivalBotManager.MapBakeEnabled && RuntimeRainNavMesh.IsHighDetail &&
                RuntimeRainNavMesh.IsBuilding)
            {
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "background_build_preserved reason=" +
                    SafeOneLine(reason, 80) + " map=" + SafeMap(RuntimeRainNavMesh.CurrentMapName));
                return;
            }
            RuntimeRainNavMesh.Deactivate(reason);
            if (releaseMemoryCache)
                RuntimeRainNavMesh.ReleaseMemoryCache("scene_exit:" + reason);
            ResetNavigationState();
        }

        private static void ResetNavigationState()
        {
            _navMapName = string.Empty;
            _navResourceDeclared = false;
            _navLoadRequested = false;
            _navState = AutoBattleNavResourceState.Unavailable;
        }

        internal static void PrepareNavigationLoad(string mapName, ref bool loadNavmesh)
        {
            string normalized = (mapName ?? string.Empty).Trim().ToLowerInvariant();
            bool original = loadNavmesh;
            bool declared = ManifestDeclaresNavMesh(normalized);
            bool bakeMode = SurvivalBotManager.MapBakeEnabled;
            bool level33Test = SurvivalBotManager.Level33TestEnabled;

            if (declared && !loadNavmesh)
                loadNavmesh = true;

            if (bakeMode && RuntimeRainNavMesh.IsHighDetail && RuntimeRainNavMesh.IsBuilding &&
                string.Equals(RuntimeRainNavMesh.CurrentMapName, normalized, StringComparison.OrdinalIgnoreCase))
            {
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "background_build_kept activeMap=" +
                    SafeMap(RuntimeRainNavMesh.CurrentMapName) + " incomingMap=" + SafeMap(normalized));
                return;
            }

            RuntimeRainNavMesh.PrepareMap(normalized, bakeMode || level33Test || !declared,
                bakeMode || level33Test);

            _navMapName = normalized;
            _navResourceDeclared = declared;
            _navLoadRequested = loadNavmesh || RuntimeRainNavMesh.Requested;
            _navLoadStartedAt = Time.realtimeSinceStartup;
            _nextNavProbeTime = 0f;
            _physicsSearchJob = null;
            _rainSearchJob = null;
            SetNavigationState(_navLoadRequested ? AutoBattleNavResourceState.Loading : AutoBattleNavResourceState.Unavailable,
                "map=" + SafeMap(normalized) +
                " manifest=" + (declared ? "hit" : "miss") +
                " original=" + (original ? "1" : "0") +
                " native=" + (loadNavmesh ? "1" : "0") +
                " runtime=" + (RuntimeRainNavMesh.Requested ? "1" : "0") +
                " bake=" + (bakeMode ? "1" : "0") +
                " forced=" + (!original && loadNavmesh ? "1" : "0"));
        }

        internal static void EnsureMapBake(Level level)
        {
            if (level == null || string.IsNullOrEmpty(level.map_name)) return;
            string deferredReason = string.Empty;
            if (MapBakeSceneLoader.IsTransitioning)
            {
                deferredReason = "scene_transition";
            }
            else
            {
                try
                {
                    GameStateManager stateManager = ASSingleton<GameStateManager>.Instance;
                    if (stateManager != null && stateManager.CurStateType == GameStateType.Lobby)
                        deferredReason = "lobby_stale_level";
                    else if (stateManager != null && stateManager.CurStateType == GameStateType.GameLoading)
                        deferredReason = "game_loading";
                }
                catch
                {
                    deferredReason = "state_unavailable";
                }
            }
            if (string.IsNullOrEmpty(deferredReason) && MapBakeSceneLoader.DirectSceneActive &&
                !MapBakeSceneLoader.IsExpectedDirectScene(level.map_name))
                deferredReason = "unexpected_direct_scene:" + SafeMap(level.map_name);
            if (!string.IsNullOrEmpty(deferredReason))
            {
                if (!string.Equals(_mapBakeDeferredReason, deferredReason, StringComparison.Ordinal))
                {
                    _mapBakeDeferredReason = deferredReason;
                    FileLogger.Log("AUTO-BATTLE][NAVMESH", "map_bake_deferred reason=" + deferredReason);
                }
                return;
            }

            _mapBakeDeferredReason = string.Empty;
            string normalized = level.map_name.Trim().ToLowerInvariant();
            if (RuntimeRainNavMesh.IsHighDetail && RuntimeRainNavMesh.IsBuilding) return;
            if (RuntimeRainNavMesh.Requested && RuntimeRainNavMesh.IsHighDetail &&
                string.Equals(RuntimeRainNavMesh.CurrentMapName, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            RuntimeRainNavMesh.PrepareMap(normalized, true, true);
            _navMapName = normalized;
            _navResourceDeclared = ManifestDeclaresNavMesh(normalized);
            _navLoadRequested = true;
            _navLoadStartedAt = Time.realtimeSinceStartup;
            _nextNavProbeTime = 0f;
            _physicsSearchJob = null;
            _rainSearchJob = null;
            SetNavigationState(AutoBattleNavResourceState.Loading,
                "map=" + SafeMap(normalized) + " provider=runtime profile=max_detail source=map_bake");
        }

        private static bool ManifestDeclaresNavMesh(string mapName)
        {
            if (string.IsNullOrEmpty(mapName)) return false;
            if (!_manifestRead)
            {
                _manifestRead = true;
                try
                {
                    string path = Path.Combine(Application.dataPath, "FileInfo.xml");
                    _manifestText = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
                    FileLogger.Log("AUTO-BATTLE][NAVMESH", "manifest=" + path + " loaded=" + (!string.IsNullOrEmpty(_manifestText) ? "1" : "0"));
                }
                catch (Exception ex)
                {
                    _manifestText = string.Empty;
                    FileLogger.Log("AUTO-BATTLE][NAVMESH", "manifest_read_failed=" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 120));
                }
            }

            string needle = "Prefab/NavMesh/" + mapName + ".navmesh";
            return !string.IsNullOrEmpty(_manifestText) &&
                   _manifestText.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static AutoBattleRouteResult BuildRoute(Vector3 from, Vector3 to, Transform ignoreRoot)
        {
            return BuildRoute(from, to, ignoreRoot, null);
        }

        public static AutoBattleRouteResult BuildRoute(Vector3 from, Vector3 to, Transform ignoreRoot, AutoBattleRouteCapabilities capabilities)
        {
            AutoBattleRouteResult route;
            List<Vector3> points;
            if (capabilities == null) capabilities = new AutoBattleRouteCapabilities();
            capabilities.RequireRainPath = true;
            string rainDetail = IsGameNavigationReady ? "ready" : RuntimeRainNavMesh.Detail;

            if (IsGameNavigationReady)
            {
                bool rainPending;
                bool rainPartial;
                bool rainOffMesh;
                List<bool> rainOffMeshFlags;
                if (TryBuildRainPath(from, to, capabilities, out points, out rainDetail,
                    out rainPending, out rainPartial, out rainOffMesh, out rainOffMeshFlags))
                {
                    string optimizeDetail;
                    if (rainOffMesh)
                    {
                        List<bool> optimizedFlags;
                        points = OptimizeRainPathWithHardLinks(from, points, rainOffMeshFlags,
                            ignoreRoot, out optimizedFlags, out optimizeDetail);
                        rainOffMeshFlags = optimizedFlags;
                    }
                    else
                    {
                        points = OptimizeRainPath(from, points, ignoreRoot, out optimizeDetail);
                    }
                    rainDetail += " " + optimizeDetail;
                    string validationDetail = "not_checked";
                    List<bool> validatedJumpFlags = new List<bool>();
                    bool physicsValidated = !rainPartial && ValidateRainPath(from, points, capabilities,
                        ignoreRoot, rainOffMeshFlags, out validatedJumpFlags, out validationDetail);
                    if (physicsValidated)
                    {
                        _physicsSearchJob = null;
                        route = FromPoints("rain_navmesh", false, points,
                            rainDetail + " validate=" + validationDetail);
                        for (int i = 0; i < route.JumpFlags.Count && i < validatedJumpFlags.Count; i++)
                            route.JumpFlags[i] = validatedJumpFlags[i];
                        AnnotateBuiltInJumpFlags(route, from, capabilities, ignoreRoot);
                        LogRoute(route);
                        return route;
                    }
                    rainDetail += rainPartial ? " validate=partial_rejected" : " validate=" + validationDetail;
                }
                else if (rainPending)
                {
                    route = Pending("rain_navmesh_pending", rainDetail);
                    LogRoute(route);
                    return route;
                }

                _physicsSearchJob = null;
                route = Fail("rain_navmesh_required",
                    "result=fail reason=complete_rain_path_unavailable " + rainDetail);
                LogRoute(route);
                return route;
            }
            else
            {
                _physicsSearchJob = null;
                route = Pending("rain_navmesh_pending",
                    "result=pending reason=navigation_not_ready state=" + NavigationState);
                LogRoute(route);
                return route;
            }
        }

        private static void AnnotateBuiltInJumpFlags(AutoBattleRouteResult route, Vector3 from, AutoBattleRouteCapabilities capabilities, Transform ignoreRoot)
        {
            if (route == null || route.Corners.Count == 0) return;
            Vector3 previous = from;
            int jumps = 0;
            for (int i = 0; i < route.Corners.Count; i++)
            {
                Vector3 point = route.Corners[i];
                float horizontal = XZDistance(previous, point);
                bool walkable = CanTraverseWalkableSurface(previous, point, ignoreRoot);
                float rise = point.y - previous.y;
                bool jump = i < route.JumpFlags.Count && route.JumpFlags[i];
                jump = jump || (capabilities.AllowJump &&
                                !walkable &&
                                rise > 0.72f &&
                                horizontal <= 4.2f &&
                                TryJumpSegment(previous, point, capabilities, ignoreRoot));
                if (i < route.JumpFlags.Count) route.JumpFlags[i] = jump;
                if (jump) jumps++;
                previous = point;
            }
            if (jumps > 0) route.Detail += " inferredJumps=" + jumps;
        }

        private static List<Vector3> OptimizeRainPath(Vector3 from, List<Vector3> raw,
            Transform ignoreRoot, out string detail)
        {
            Stopwatch timer = Stopwatch.StartNew();
            int rawCount = raw == null ? 0 : raw.Count;
            List<Vector3> clean = new List<Vector3>(rawCount);
            if (raw != null)
            {
                for (int i = 0; i < raw.Count; i++)
                {
                    Vector3 point = raw[i];
                    if (!IsFinite(point)) continue;
                    if (clean.Count > 0 && XZDistance(clean[clean.Count - 1], point) < 0.18f &&
                        Mathf.Abs(clean[clean.Count - 1].y - point.y) < 0.28f)
                        continue;
                    clean.Add(point);
                }
            }

            List<Vector3> simplified = new List<Vector3>(clean.Count);
            Vector3 anchor = from;
            int cursor = 0;
            int shortcuts = 0;
            while (cursor < clean.Count)
            {
                int selected = cursor;
                int furthest = Mathf.Min(clean.Count - 1, cursor + 6);
                for (int candidate = furthest; candidate > cursor; candidate--)
                {
                    if (!RainShortcutFollowsRawCorridor(anchor, clean, cursor, candidate,
                        RainShortcutCorridorRadius)) continue;
                    if (!CanTraverseWalkableSurface(anchor, clean[candidate], ignoreRoot) ||
                        !HasNavigationBodyClearance(anchor, clean[candidate], ignoreRoot,
                            NavigationBodyRadius)) continue;
                    selected = candidate;
                    break;
                }
                if (selected > cursor) shortcuts += selected - cursor;
                simplified.Add(clean[selected]);
                anchor = clean[selected];
                cursor = selected + 1;
            }

            int adjustedCorners = 0;
            for (int i = 1; i + 1 < simplified.Count; i++)
            {
                Vector3 incoming = simplified[i] - simplified[i - 1];
                Vector3 outgoing = simplified[i + 1] - simplified[i];
                incoming.y = 0f;
                outgoing.y = 0f;
                if (incoming.sqrMagnitude < 0.36f || outgoing.sqrMagnitude < 0.36f) continue;
                incoming.Normalize();
                outgoing.Normalize();
                bool sharpTurn = Vector3.Dot(incoming, outgoing) <= 0.92f;
                if (!sharpTurn && MeasureWallClearance(simplified[i], ignoreRoot) >= 0.78f) continue;

                Vector3 expanded;
                if (TryExpandRainCorner(simplified[i - 1], simplified[i], simplified[i + 1],
                    ignoreRoot, out expanded))
                {
                    simplified[i] = expanded;
                    adjustedCorners++;
                }
            }

            timer.Stop();
            detail = "opt=rain raw=" + rawCount + " clean=" + clean.Count +
                      " smooth=" + simplified.Count + " corners=" + adjustedCorners +
                      " shortcuts=" + shortcuts +
                      " body=" + NavigationBodyRadius.ToString("0.00") +
                      " corridor=" + RainShortcutCorridorRadius.ToString("0.00") +
                      " optMs=" + timer.ElapsedMilliseconds;
            return simplified;
        }

        private static bool RainShortcutFollowsRawCorridor(Vector3 from, List<Vector3> points,
            int first, int last, float maxDeviation)
        {
            if (points == null || first < 0 || last >= points.Count || first > last) return false;
            Vector3 to = points[last];
            Vector2 segment = new Vector2(to.x - from.x, to.z - from.z);
            float lengthSq = segment.sqrMagnitude;
            if (lengthSq < 0.01f) return false;
            float maxDeviationSq = maxDeviation * maxDeviation;
            for (int i = first; i < last; i++)
            {
                Vector2 relative = new Vector2(points[i].x - from.x, points[i].z - from.z);
                float t = Mathf.Clamp01(Vector2.Dot(relative, segment) / lengthSq);
                Vector2 nearest = segment * t;
                if ((relative - nearest).sqrMagnitude > maxDeviationSq) return false;
            }
            return true;
        }

        private static bool TryExpandRainCorner(Vector3 previous, Vector3 corner, Vector3 next,
            Transform ignoreRoot, out Vector3 expanded)
        {
            expanded = corner;
            float baselineClearance = MeasureWallClearance(corner, ignoreRoot);
            float bestScore = baselineClearance * 3f;
            bool found = false;
            const int directions = 12;
            for (int ring = 0; ring < 2; ring++)
            {
                float radius = RainCornerClearanceRadius + ring * 0.28f;
                for (int i = 0; i < directions; i++)
                {
                    float angle = i * (360f / directions) * Mathf.Deg2Rad;
                    Vector3 raw = corner + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                    Vector3 candidate;
                    if (!TrySnapToGroundNear(raw, corner.y, 1.25f, out candidate, false)) continue;
                    if (!IsPointOnOwnedRainGraph(candidate, 1.0f)) continue;
                    if (!HasStandingSpace(candidate, ignoreRoot)) continue;
                    if (!CanTraverseWalkableSurface(previous, candidate, ignoreRoot) ||
                        !CanTraverseWalkableSurface(candidate, next, ignoreRoot) ||
                        !HasNavigationBodyClearance(previous, candidate, ignoreRoot,
                            NavigationBodyRadius) ||
                        !HasNavigationBodyClearance(candidate, next, ignoreRoot,
                            NavigationBodyRadius))
                        continue;

                    float clearance = MeasureWallClearance(candidate, ignoreRoot);
                    if (clearance < NavigationBodyRadius + 0.10f) continue;
                    float detour = XZDistance(previous, candidate) + XZDistance(candidate, next) -
                                   XZDistance(previous, corner) - XZDistance(corner, next);
                    float score = clearance * 3f - radius * 0.35f - Mathf.Max(0f, detour) * 0.18f;
                    if (score <= bestScore + 0.12f) continue;
                    bestScore = score;
                    expanded = candidate;
                    found = true;
                }
            }
            return found;
        }

        public static bool CanTraverseWalkableSurface(Vector3 from, Vector3 to, Transform ignoreRoot)
        {
            if (HasWalkSegment(from, to, ignoreRoot) && HasGroundSupportSegment(from, to, ignoreRoot))
                return true;
            return IsContinuousWalkableRamp(from, to, ignoreRoot);
        }

        public static bool TryFindRainClearanceDirection(Vector3 from, Vector3 desiredDirection,
            Transform ignoreRoot, out Vector3 direction, out string detail)
        {
            direction = Vector3.zero;
            detail = "rain_clearance=none";
            desiredDirection.y = 0f;
            if (desiredDirection.sqrMagnitude < 0.01f) return false;
            desiredDirection.Normalize();

            float[] angles = { 48f, -48f, 72f, -72f, 96f, -96f, 132f, -132f, 180f };
            float bestScore = float.MinValue;
            float bestAngle = 0f;
            for (int i = 0; i < angles.Length; i++)
            {
                Vector3 candidateDirection = Quaternion.Euler(0f, angles[i], 0f) * desiredDirection;
                Vector3 raw = from + candidateDirection * 1.65f;
                Vector3 grounded;
                if (!TrySnapToGroundNear(raw, from.y, 1.35f, out grounded, false)) continue;
                if (!IsPointOnOwnedRainGraph(grounded, 1.0f)) continue;
                if (!CanTraverseWalkableSurface(from, grounded, ignoreRoot)) continue;
                if (!HasNavigationBodyClearance(from, grounded, ignoreRoot, NavigationBodyRadius)) continue;
                float clearance = MeasureWallClearance(grounded, ignoreRoot);
                if (clearance < NavigationBodyRadius + 0.10f) continue;

                Vector3 move = grounded - from;
                move.y = 0f;
                if (move.sqrMagnitude < 0.16f) continue;
                move.Normalize();
                float score = Vector3.Dot(move, desiredDirection) * 1.4f +
                              clearance * 0.8f;
                if (score <= bestScore) continue;
                bestScore = score;
                bestAngle = angles[i];
                direction = move;
            }

            if (direction.sqrMagnitude < 0.01f) return false;
            detail = "rain_clearance=ok angle=" + bestAngle.ToString("0") +
                     " score=" + bestScore.ToString("0.00");
            return true;
        }

        private static bool IsContinuousWalkableRamp(Vector3 from, Vector3 to, Transform ignoreRoot)
        {
            float horizontal = XZDistance(from, to);
            if (horizontal < 0.18f || horizontal > 14f) return false;
            float grade = Mathf.Abs(to.y - from.y) / horizontal;
            if (grade > MaxWalkableRampGrade) return false;

            int samples = Mathf.Clamp(Mathf.CeilToInt(horizontal / 0.42f), 2, 40);
            Vector3 previousGround = Vector3.zero;
            bool hasPrevious = false;
            for (int i = 0; i <= samples; i++)
            {
                float t = (float)i / samples;
                Vector3 expected = Vector3.Lerp(from, to, t);
                Vector3 ground;
                Vector3 normal;
                if (!TryFindRampGround(expected, ignoreRoot, out ground, out normal)) return false;
                if (normal.y < 0.52f) return false;
                if (i > 0 && i < samples && i % 3 == 0 && !HasStandingSpace(ground, ignoreRoot))
                    return false;
                if (hasPrevious)
                {
                    float run = XZDistance(previousGround, ground);
                    if (run < 0.08f || Mathf.Abs(ground.y - previousGround.y) > run * 1.08f + 0.20f)
                        return false;
                    if (HasWallBetweenGroundPoints(previousGround, ground, ignoreRoot)) return false;
                }
                previousGround = ground;
                hasPrevious = true;
            }
            return true;
        }

        private static bool TryFindRampGround(Vector3 expected, Transform ignoreRoot,
            out Vector3 ground, out Vector3 normal)
        {
            ground = expected;
            normal = Vector3.zero;
            try
            {
                RaycastHit[] hits = Physics.RaycastAll(expected + Vector3.up * 1.25f,
                    Vector3.down, 2.5f, GroundMask);
                float bestDelta = 0.9f;
                bool found = false;
                for (int i = 0; hits != null && i < hits.Length; i++)
                {
                    RaycastHit hit = hits[i];
                    if (hit.collider == null || hit.collider.isTrigger ||
                        IsIgnored(hit.collider.transform, ignoreRoot) || hit.normal.y < 0.35f)
                        continue;
                    float delta = Mathf.Abs(hit.point.y - expected.y);
                    if (delta > bestDelta) continue;
                    bestDelta = delta;
                    ground = hit.point;
                    normal = hit.normal;
                    found = true;
                }
                return found;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasWallBetweenGroundPoints(Vector3 from, Vector3 to, Transform ignoreRoot)
        {
            Vector3 segment = to - from;
            float distance = segment.magnitude;
            if (distance < 0.08f) return false;
            Vector3 direction = segment / distance;
            float[] heights = { 0.42f, 1.05f };
            for (int h = 0; h < heights.Length; h++)
            {
                RaycastHit[] hits = Physics.RaycastAll(from + Vector3.up * heights[h],
                    direction, distance, BlockMask);
                for (int i = 0; hits != null && i < hits.Length; i++)
                {
                    RaycastHit hit = hits[i];
                    if (hit.collider == null || hit.collider.isTrigger ||
                        IsIgnored(hit.collider.transform, ignoreRoot))
                        continue;
                    if (hit.normal.y < 0.48f) return true;
                }
            }
            return false;
        }

        private static float MeasureWallClearance(Vector3 point, Transform ignoreRoot)
        {
            const float maxDistance = 1.35f;
            float clearance = maxDistance;
            try
            {
                for (int i = 0; i < 12; i++)
                {
                    float angle = i * 30f * Mathf.Deg2Rad;
                    Vector3 direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                    RaycastHit[] hits = Physics.RaycastAll(point + Vector3.up * 0.92f,
                        direction, maxDistance, BlockMask);
                    for (int h = 0; hits != null && h < hits.Length; h++)
                    {
                        RaycastHit hit = hits[h];
                        if (hit.collider == null || hit.collider.isTrigger ||
                            IsIgnored(hit.collider.transform, ignoreRoot) || hit.normal.y >= 0.48f)
                            continue;
                        clearance = Mathf.Min(clearance, hit.distance);
                    }
                }
            }
            catch
            {
                return 0f;
            }
            return clearance;
        }

        private static bool HasDeepSameLevelDetour(List<Vector3> points, Vector3 from, Vector3 to, out float dropDepth)
        {
            dropDepth = 0f;
            if (points == null || points.Count == 0 || Mathf.Abs(from.y - to.y) > 1.6f) return false;

            float minimumY = Mathf.Min(from.y, to.y);
            for (int i = 0; i < points.Count; i++)
                minimumY = Mathf.Min(minimumY, points[i].y);

            dropDepth = Mathf.Min(from.y, to.y) - minimumY;
            return dropDepth > 2.0f;
        }

        public static float CandidatePenalty(Vector3 from, Vector3 to, Transform ignoreRoot)
        {
            Vector3 snapped;
            if (!TrySnapToGround(to, out snapped, true)) return 220f;
            if (Mathf.Abs(snapped.y - from.y) > 4.0f) return 120f;
            if (HasWalkSegment(from, snapped, ignoreRoot) && HasCandidateGroundSupportSegment(from, snapped)) return 0f;
            return 18f;
        }

        private static bool HasCandidateGroundSupportSegment(Vector3 from, Vector3 to)
        {
            float distance = XZDistance(from, to);
            if (distance < 0.65f) return true;
            int samples = Mathf.Clamp(Mathf.CeilToInt(distance / 2.5f), 2, 8);
            try
            {
                for (int i = 1; i < samples; i++)
                {
                    float t = (float)i / samples;
                    Vector3 expected = Vector3.Lerp(from, to, t);
                    RaycastHit hit;
                    if (!Physics.Raycast(expected + Vector3.up * 0.9f, Vector3.down, out hit, 1.9f, GroundMask))
                        return false;
                    if (Mathf.Abs(hit.point.y - expected.y) > 0.72f) return false;
                }
                return true;
            }
            catch { return false; }
        }

        public static bool HasForwardBlock(Vector3 from, Vector3 dir, Transform ignoreRoot)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) return false;
            dir.Normalize();
            Vector3 to = from + dir * 1.05f;
            return !CanTraverseWalkableSurface(from, to, ignoreRoot) ||
                   !HasNavigationBodyClearance(from, to, ignoreRoot, NavigationBodyRadius, false);
        }

        public static bool CanFollowSegment(Vector3 from, Vector3 to, Transform ignoreRoot)
        {
            return CanTraverseWalkableSurface(from, to, ignoreRoot) &&
                   HasNavigationBodyClearance(from, to, ignoreRoot, NavigationBodyRadius);
        }

        private static bool HasNavigationBodyClearance(Vector3 from, Vector3 to,
            Transform ignoreRoot, float radius)
        {
            return HasNavigationBodyClearance(from, to, ignoreRoot, radius, true);
        }

        private static bool HasNavigationBodyClearance(Vector3 from, Vector3 to,
            Transform ignoreRoot, float radius, bool sampleRadialClearance)
        {
            Vector3 flat = to - from;
            flat.y = 0f;
            float distance = flat.magnitude;
            if (distance < 0.08f) return true;
            Vector3 direction = flat / distance;
            Vector3 side = Vector3.Cross(Vector3.up, direction).normalized;
            try
            {
                for (int o = 0; o < NavigationProbeOffsetScales.Length; o++)
                {
                    for (int h = 0; h < NavigationProbeHeights.Length; h++)
                    {
                        Vector3 origin = from + side * (NavigationProbeOffsetScales[o] * radius) +
                                         Vector3.up * NavigationProbeHeights[h];
                        RaycastHit[] hits = Physics.RaycastAll(origin, direction, distance, BlockMask);
                        if (HasBlockingWallHit(hits, ignoreRoot)) return false;
                    }
                }

                if (!sampleRadialClearance) return true;
                if (MeasureWallClearance(to, ignoreRoot) < radius) return false;
                if (distance > 2.5f &&
                    MeasureWallClearance(Vector3.Lerp(from, to, 0.5f), ignoreRoot) < radius)
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasBlockingWallHit(RaycastHit[] hits, Transform ignoreRoot)
        {
            if (hits == null) return false;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null || collider.isTrigger ||
                    IsIgnored(collider.transform, ignoreRoot)) continue;
                if (hits[i].normal.y < 0.55f) return true;
            }
            return false;
        }

        public static bool ShouldJumpForwardObstacle(Vector3 from, Vector3 dir, Transform ignoreRoot)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) return false;
            dir.Normalize();
            try
            {
                RaycastHit[] lowHits = Physics.RaycastAll(from + Vector3.up * 0.34f, dir, 1.15f, BlockMask);
                if (!HasNonIgnoredHit(lowHits, ignoreRoot)) return false;

                RaycastHit[] chestHits = Physics.RaycastAll(from + Vector3.up * 1.12f, dir, 1.25f, BlockMask);
                if (HasNonIgnoredHit(chestHits, ignoreRoot)) return false;

                Vector3 landing;
                if (!TrySnapToGround(from + dir * 1.45f, out landing, false)) return false;
                float rise = landing.y - from.y;
                return rise >= -0.65f && rise <= 1.15f && HasStandingSpace(landing, ignoreRoot);
            }
            catch
            {
                return false;
            }
        }

        public static bool CanExecuteJump(Vector3 from, Vector3 to, AutoBattleRouteCapabilities capabilities, Transform ignoreRoot)
        {
            return TryJumpSegment(from, to, capabilities ?? new AutoBattleRouteCapabilities(), ignoreRoot);
        }

        public static bool HasWalkSegment(Vector3 from, Vector3 to, Transform ignoreRoot)
        {
            Vector3 a = from;
            Vector3 b = to;
            if (Mathf.Abs(a.y - b.y) > MaxStepHeight) return false;
            Vector3 delta = b - a;
            delta.y = 0f;
            float dist = delta.magnitude;
            if (dist < 0.08f) return true;
            Vector3 dir = delta / dist;
            for (int i = 0; i < WalkProbeHeights.Length; i++)
            {
                Vector3 origin = new Vector3(a.x, Mathf.Min(a.y, b.y), a.z) + Vector3.up * WalkProbeHeights[i];
                RaycastHit[] hits = Physics.RaycastAll(origin, dir, dist, BlockMask);
                if (HasNonIgnoredHit(hits, ignoreRoot)) return false;
            }
            return true;
        }

        private static AutoBattleRouteResult BuildPhysicsGridRoute(Vector3 from, Vector3 to, Transform ignoreRoot, AutoBattleRouteCapabilities capabilities, string unityDetail, string astarDetail, string rainDetail)
        {
            PhysicsSearchJob job = _physicsSearchJob;
            if (job == null || !job.Matches(from, to, ignoreRoot))
            {
                Vector3 start;
                Vector3 goal;
                if (!TrySnapToGround(from, out start, true))
                {
                    _physicsSearchJob = null;
                    return Fail("phys_grid_2_5d", "result=fail nodes=0 layers=0 jumps=0 corners=0 ms=0 rejectGround=1 rejectBlock=0 frontier=0 reason=start_no_ground astar=" + astarDetail + " rain=" + rainDetail + " unity=" + unityDetail);
                }

                PhysStats stats = new PhysStats();
                if (!TrySnapToGroundNear(to, to.y, Mathf.Max(8.0f, capabilities.JumpHeight + 1.8f), out goal, true))
                {
                    goal = to;
                    stats.RejectGround++;
                }

                job = new PhysicsSearchJob();
                job.QueryFrom = from;
                job.QueryTo = to;
                job.IgnoreRoot = ignoreRoot;
                job.Capabilities = capabilities;
                job.UnityDetail = unityDetail;
                job.AstarDetail = astarDetail;
                job.RainDetail = rainDetail;
                job.Start = start;
                job.Goal = goal;
                job.Stats = stats;
                float direct = XZDistance(start, goal);
                float maxRadius = Mathf.Clamp(direct + 24f, 22f, MaxRouteRadius);
                job.MaxGrid = Mathf.CeilToInt(maxRadius / CellSize) + 2;
                job.GoalX = WorldToGridX(goal, start);
                job.GoalZ = WorldToGridZ(goal, start);

                GridNode first = new GridNode();
                first.X = 0;
                first.Z = 0;
                first.Layer = HeightLayer(start.y);
                first.Key = new GridKey(first.X, first.Z, first.Layer);
                first.Pos = start;
                first.G = 0f;
                first.H = Heuristic(start, goal);
                job.First = first;
                job.Best = first;
                job.All[first.Key] = first;
                job.LayerCounts[Key(0, 0)] = 1;
                job.Open.Push(first);
                job.HeightLayers.Add(first.Layer);
                _physicsSearchJob = job;
            }

            float frameMilliseconds;
            float frameEma;
            int nodeBudget;
            float sliceBudget = AcquireSliceBudget(out frameMilliseconds, out frameEma, out nodeBudget);
            if (sliceBudget < 0.5f)
            {
                job.LastSliceMilliseconds = 0L;
                job.LastSliceBudgetMilliseconds = 0f;
                job.LastSliceNodes = 0;
                job.BudgetSkips++;
                return Pending("phys_grid_2_5d_pending",
                    PendingDetail(job, frameMilliseconds, frameEma, "frame_budget_exhausted"));
            }

            Stopwatch slice = Stopwatch.StartNew();
            int expandedThisSlice = 0;
            while (job.Open.Count > 0 && job.Expanded < MaxNodes &&
                   expandedThisSlice < nodeBudget &&
                   (expandedThisSlice == 0 || slice.Elapsed.TotalMilliseconds < sliceBudget))
            {
                GridNode current = job.Open.Pop();
                if (current == null || current.Closed) continue;
                current.Closed = true;
                job.Expanded++;
                expandedThisSlice++;

                if (IsBetterFrontier(current, job.Best, job.Goal)) job.Best = current;

                float heightError = Mathf.Abs(current.Pos.y - job.Goal.y);
                float goalHorizontal = XZDistance(current.Pos, job.Goal);
                if (goalHorizontal <= GoalTolerance && heightError <= 0.65f &&
                    (goalHorizontal <= 0.30f || CanFollowSegment(current.Pos, job.Goal, ignoreRoot)))
                {
                    job.Found = current;
                    break;
                }

                if (goalHorizontal > 0.30f && heightError <= MaxStepHeight &&
                    CanFollowSegment(current.Pos, job.Goal, ignoreRoot))
                {
                    job.Found = current;
                    break;
                }

                if (capabilities.AllowJump && goalHorizontal > 0.30f && TryJumpSegment(current.Pos, job.Goal, capabilities, ignoreRoot))
                {
                    GridNode jumpGoal = new GridNode();
                    jumpGoal.X = job.GoalX;
                    jumpGoal.Z = job.GoalZ;
                    jumpGoal.Layer = HeightLayer(job.Goal.y);
                    jumpGoal.Key = new GridKey(job.GoalX, job.GoalZ, jumpGoal.Layer);
                    jumpGoal.Pos = job.Goal;
                    jumpGoal.G = current.G + goalHorizontal + 2.0f;
                    jumpGoal.H = 0f;
                    jumpGoal.Parent = current;
                    jumpGoal.JumpFromParent = true;
                    job.Found = jumpGoal;
                    job.JumpLinks++;
                    break;
                }

                int normalLinks = ExpandNeighbors(current, 1, job, ignoreRoot);
                if (capabilities.AllowJump && normalLinks <= 2)
                {
                    ExpandNeighbors(current, 2, job, ignoreRoot);
                }
            }

            slice.Stop();
            float actualSliceMilliseconds = (float)slice.Elapsed.TotalMilliseconds;
            RecordSliceCost(actualSliceMilliseconds);
            job.LastSliceMilliseconds = (long)Math.Ceiling(actualSliceMilliseconds);
            job.LastSliceBudgetMilliseconds = sliceBudget;
            job.LastSliceNodes = expandedThisSlice;
            job.LastFrameMilliseconds = frameMilliseconds;
            job.LastFrameEma = frameEma;
            job.CpuMilliseconds += job.LastSliceMilliseconds;
            job.Slices++;

            bool complete = job.Found != null || job.Open.Count == 0 || job.Expanded >= MaxNodes;
            if (!complete)
            {
                return Pending("phys_grid_2_5d_pending",
                    PendingDetail(job, frameMilliseconds, frameEma, "searching"));
            }

            _physicsSearchJob = null;
            bool partial = false;
            GridNode end = job.Found;
            if (end == null)
            {
                end = job.Best;
                partial = end != null && end != job.First;
            }

            if (end == null || end == job.First)
            {
                return Fail("phys_grid_2_5d", "result=fail nodes=" + job.Expanded + " layers=" + job.HeightLayers.Count + " jumps=" + job.JumpLinks + " corners=0 slices=" + job.Slices + " sliceMs=" + job.LastSliceMilliseconds + " sliceBudgetMs=" + job.LastSliceBudgetMilliseconds.ToString("0.0") + " sliceNodes=" + job.LastSliceNodes + " frameMs=" + job.LastFrameMilliseconds.ToString("0.0") + " frameEma=" + job.LastFrameEma.ToString("0.0") + " ms=" + job.CpuMilliseconds + CacheDetail(job) + " rejectGround=" + job.Stats.RejectGround + " rejectBlock=" + job.Stats.RejectBlock + " frontier=0 reason=no_frontier astar=" + job.AstarDetail + " rain=" + job.RainDetail + " unity=" + job.UnityDetail);
            }

            List<RouteStep> rawPath = Reconstruct(end);
            if (!partial && (XZDistance(rawPath[rawPath.Count - 1].Pos, job.Goal) > 0.10f || Mathf.Abs(rawPath[rawPath.Count - 1].Pos.y - job.Goal.y) > 0.10f) &&
                Mathf.Abs(rawPath[rawPath.Count - 1].Pos.y - job.Goal.y) <= MaxStepHeight &&
                CanFollowSegment(rawPath[rawPath.Count - 1].Pos, job.Goal, ignoreRoot))
            {
                rawPath.Add(new RouteStep(job.Goal, false));
            }

            List<RouteStep> smooth = SmoothPath(rawPath, ignoreRoot);
            return FromSteps("phys_grid_2_5d", partial, smooth,
                "result=" + (partial ? "partial" : "ok") +
                " nodes=" + job.Expanded +
                " layers=" + job.HeightLayers.Count +
                " jumps=" + CountJumpSteps(smooth) +
                " jumpLinks=" + job.JumpLinks +
                " corners=" + smooth.Count +
                " slices=" + job.Slices +
                " budgetSkips=" + job.BudgetSkips +
                " sliceMs=" + job.LastSliceMilliseconds +
                " sliceBudgetMs=" + job.LastSliceBudgetMilliseconds.ToString("0.0") +
                " sliceNodes=" + job.LastSliceNodes +
                " frameMs=" + job.LastFrameMilliseconds.ToString("0.0") +
                " frameEma=" + job.LastFrameEma.ToString("0.0") +
                " ms=" + job.CpuMilliseconds +
                CacheDetail(job) +
                " rejectGround=" + job.Stats.RejectGround +
                " rejectBlock=" + job.Stats.RejectBlock +
                " frontier=" + (partial ? "1" : "0") +
                " budget=full" +
                " endDist=" + XZDistance(smooth[smooth.Count - 1].Pos, job.Goal).ToString("0.0") +
                " endY=" + Mathf.Abs(smooth[smooth.Count - 1].Pos.y - job.Goal.y).ToString("0.0") +
                " astar=" + job.AstarDetail + " rain=" + job.RainDetail + " unity=" + job.UnityDetail);
        }

        private static float AcquireSliceBudget(out float frameMilliseconds, out float frameEma, out int nodeBudget)
        {
            frameMilliseconds = Mathf.Clamp(Time.unscaledDeltaTime * 1000f, 0.1f, 100f);
            int frame = Time.frameCount;
            if (_sliceBudgetFrame != frame)
            {
                _sliceBudgetFrame = frame;
                _frameMillisecondsEma = Mathf.Lerp(_frameMillisecondsEma, frameMilliseconds, 0.18f);
                float headroom = Mathf.Max(0f, TargetFrameMilliseconds - _frameMillisecondsEma);
                _sliceBudgetMilliseconds = Mathf.Clamp(8f + headroom * 0.65f,
                    MinSearchSliceMilliseconds, MaxSearchSliceMilliseconds);
                if (frameMilliseconds >= 30f) _sliceBudgetMilliseconds = MinSearchSliceMilliseconds;
                else if (frameMilliseconds >= 22f) _sliceBudgetMilliseconds = Mathf.Min(10f, _sliceBudgetMilliseconds);
                _sliceSpentMilliseconds = 0f;
            }

            frameEma = _frameMillisecondsEma;
            float remaining = Mathf.Max(0f, _sliceBudgetMilliseconds - _sliceSpentMilliseconds);
            nodeBudget = Mathf.Clamp(Mathf.CeilToInt(remaining * 72f), 160, MaxNodesPerSlice);
            return remaining;
        }

        private static void RecordSliceCost(float milliseconds)
        {
            _sliceSpentMilliseconds += Mathf.Max(0f, milliseconds);
        }

        private static string PendingDetail(PhysicsSearchJob job, float frameMilliseconds, float frameEma, string reason)
        {
            return "result=pending nodes=" + job.Expanded +
                   " layers=" + job.HeightLayers.Count +
                   " jumpLinks=" + job.JumpLinks +
                   " slices=" + job.Slices +
                   " budgetSkips=" + job.BudgetSkips +
                   " sliceMs=" + job.LastSliceMilliseconds +
                   " sliceBudgetMs=" + job.LastSliceBudgetMilliseconds.ToString("0.0") +
                   " sliceNodes=" + job.LastSliceNodes +
                   " frameMs=" + frameMilliseconds.ToString("0.0") +
                   " frameEma=" + frameEma.ToString("0.0") +
                   " ms=" + job.CpuMilliseconds +
                   " open=" + job.Open.Count +
                   CacheDetail(job) +
                   " reason=" + reason;
        }

        private static string CacheDetail(PhysicsSearchJob job)
        {
            return " cacheGround=" + job.Stats.GroundCacheHits + "/" + job.Stats.GroundCacheMisses +
                   " cacheWalk=" + job.Stats.WalkCacheHits + "/" + job.Stats.WalkCacheMisses +
                   " cacheJump=" + job.Stats.JumpCacheHits + "/" + job.Stats.JumpCacheMisses;
        }

        private static int ExpandNeighbors(GridNode current, int gridStep, PhysicsSearchJob job, Transform ignoreRoot)
        {
            int added = 0;
            for (int i = 0; i < Dx.Length; i++)
            {
                int nx = current.X + Dx[i] * gridStep;
                int nz = current.Z + Dz[i] * gridStep;
                if (Mathf.Abs(nx) > job.MaxGrid || Mathf.Abs(nz) > job.MaxGrid) continue;

                Vector3 raw = new Vector3(job.Start.x + nx * CellSize, current.Pos.y, job.Start.z + nz * CellSize);
                CollectGroundLayersCached(nx, nz, raw, current.Pos.y, job, ignoreRoot);
                if (job.Grounds.Count == 0)
                {
                    job.Stats.RejectGround++;
                    continue;
                }

                for (int g = 0; g < job.Grounds.Count; g++)
                {
                    Vector3 grounded = job.Grounds[g];
                    float rise = grounded.y - current.Pos.y;
                    float horizontal = XZDistance(current.Pos, grounded);
                    bool jump = gridStep > 1;
                    bool traversable = false;
                    bool walkClear = false;
                    bool groundSupported = false;
                    int layer = HeightLayer(grounded.y);
                    GridKey key = new GridKey(nx, nz, layer);
                    GridNode next;
                    if (job.All.TryGetValue(key, out next) && next.Closed) continue;

                    if (!jump && Mathf.Abs(rise) <= MaxStepHeight)
                    {
                        GetWalkSample(current.Key, key, current.Pos, grounded, job, ignoreRoot,
                            out walkClear, out groundSupported);
                    }

                    if (!jump && walkClear && groundSupported)
                    {
                        traversable = true;
                        // A supported ramp or stair run is walkable even when its sampled height rises.
                        jump = false;
                    }
                    else if (job.Capabilities.AllowJump &&
                             (gridStep > 1 || Mathf.Abs(rise) > 0.35f || !groundSupported) &&
                             GetJumpSample(current.Key, key, current.Pos, grounded, job, ignoreRoot))
                    {
                        traversable = true;
                        jump = true;
                    }

                    if (!traversable)
                    {
                        job.Stats.RejectBlock++;
                        continue;
                    }

                    float elevationCost = Mathf.Abs(rise) * 0.7f;
                    float jumpCost = jump ? 5.5f + Mathf.Max(0f, rise) * 2.2f : 0f;
                    float dropCost = rise < -1.4f ? Mathf.Abs(rise) * 2.4f : 0f;
                    float preserveLayerCost = 0f;
                    if (Mathf.Abs(job.Start.y - job.Goal.y) <= 1.6f)
                    {
                        float preferredFloor = Mathf.Min(job.Start.y, job.Goal.y) - 1.8f;
                        if (grounded.y < preferredFloor)
                            preserveLayerCost = 16f + (preferredFloor - grounded.y) * 5f;
                    }
                    float ng = current.G + horizontal + elevationCost + jumpCost + dropCost + preserveLayerCost;
                    if (next == null)
                    {
                        long xzKey = Key(nx, nz);
                        int layerCount;
                        if (job.LayerCounts.TryGetValue(xzKey, out layerCount) && layerCount >= 5)
                        {
                            job.Stats.RejectGround++;
                            continue;
                        }
                        next = new GridNode();
                        next.X = nx;
                        next.Z = nz;
                        next.Layer = layer;
                        next.Key = key;
                        next.Pos = grounded;
                        next.G = ng;
                        next.H = Heuristic(grounded, job.Goal);
                        next.Parent = current;
                        next.JumpFromParent = jump;
                        job.All[key] = next;
                        job.LayerCounts[xzKey] = layerCount + 1;
                        job.HeightLayers.Add(layer);
                        job.Open.Push(next);
                        if (jump) job.JumpLinks++;
                        added++;
                    }
                    else if (ng + 0.01f < next.G)
                    {
                        next.G = ng;
                        next.Pos = grounded;
                        next.Parent = current;
                        next.JumpFromParent = jump;
                        job.Open.Push(next);
                        added++;
                    }
                }
            }
            return added;
        }

        private static bool TryBuildUnityNavMeshPath(Vector3 from, Vector3 to, out List<Vector3> result, out string detail)
        {
            result = null;
            detail = "unity_navmesh=not_tried";
            try
            {
                Type navMeshType = FindType("UnityEngine.NavMesh");
                Type navMeshPathType = FindType("UnityEngine.NavMeshPath");
                if (navMeshType == null || navMeshPathType == null)
                {
                    detail = "unity_navmesh=no_api";
                    return false;
                }

                object pathObj = Activator.CreateInstance(navMeshPathType);
                MethodInfo calculate = null;
                MethodInfo[] methods = navMeshType.GetMethods(BindingFlags.Static | BindingFlags.Public);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name != "CalculatePath") continue;
                    ParameterInfo[] ps = methods[i].GetParameters();
                    if (ps.Length == 4 && ps[0].ParameterType == typeof(Vector3) && ps[1].ParameterType == typeof(Vector3))
                    {
                        calculate = methods[i];
                        break;
                    }
                }
                if (calculate == null)
                {
                    detail = "unity_navmesh=no_calculate";
                    return false;
                }

                object okObj = calculate.Invoke(null, new object[] { from, to, -1, pathObj });
                bool ok = okObj is bool && (bool)okObj;
                Vector3[] corners = ReadCorners(pathObj);
                if (!ok || corners == null || corners.Length == 0)
                {
                    detail = "unity_navmesh=fail ok=" + (ok ? "1" : "0") + " corners=" + (corners == null ? 0 : corners.Length);
                    return false;
                }

                result = new List<Vector3>(corners.Length);
                for (int i = 0; i < corners.Length; i++) result.Add(corners[i]);
                detail = "unity_navmesh=ok pts=" + result.Count + " status=" + ReadStatus(pathObj);
                return result.Count > 0;
            }
            catch (Exception ex)
            {
                detail = "unity_navmesh=ex:" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 96);
                return false;
            }
        }

        private static bool TryBuildAstarPath(Vector3 from, Vector3 to, out List<Vector3> result, out string detail)
        {
            result = null;
            detail = "astar=not_tried";
            try
            {
                if (AstarPath.active == null)
                {
                    detail = "astar=no_active";
                    return false;
                }

                Pathfinding.NavGraph[] graphs = AstarPath.active.graphs;
                if (graphs == null || graphs.Length == 0)
                {
                    detail = "astar=no_graphs";
                    return false;
                }

                Pathfinding.ABPath path = Pathfinding.ABPath.Construct(from, to, null);
                if (path == null)
                {
                    detail = "astar=construct_null";
                    return false;
                }

                AstarPath.StartPath(path);
                AstarPath.WaitForPath(path);
                if (path.error)
                {
                    detail = "astar=error:" + SafeOneLine(path.errorLog, 120);
                    return false;
                }

                if (path.vectorPath == null || path.vectorPath.Count == 0)
                {
                    detail = "astar=empty";
                    return false;
                }

                result = new List<Vector3>(path.vectorPath.Count);
                for (int i = 0; i < path.vectorPath.Count; i++) result.Add(path.vectorPath[i]);
                detail = "astar=ok pts=" + result.Count + " len=" + path.GetTotalLength().ToString("0.0");
                return result.Count > 0;
            }
            catch (Exception ex)
            {
                detail = "astar=ex:" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 96);
                return false;
            }
        }

        private static bool TryBuildRainPath(Vector3 from, Vector3 to, AutoBattleRouteCapabilities capabilities,
            out List<Vector3> result, out string detail, out bool pending, out bool partial, out bool offMesh,
            out List<bool> offMeshFlags)
        {
            result = null;
            detail = "rain=not_tried";
            pending = false;
            partial = false;
            offMesh = false;
            offMeshFlags = null;
            try
            {
                if (NavigationManager.Instance == null)
                {
                    _rainSearchJob = null;
                    detail = "rain=no_instance";
                    return false;
                }
                IList<string> tags = null;
                List<RAINNavigationGraph> graphs = NavigationManager.Instance.GraphsForPoints(from, to, 4f, NavigationManager.GraphType.Navmesh, tags);
                if (graphs == null || graphs.Count == 0)
                {
                    _rainSearchJob = null;
                    detail = "rain=no_graph_for_points";
                    return false;
                }

                RAINNavigationGraph graph = graphs[0];
                if (RuntimeRainNavMesh.Requested)
                {
                    RAINNavigationGraph ownedGraph = RuntimeRainNavMesh.OwnedGraph;
                    if (ownedGraph == null || !graphs.Contains(ownedGraph))
                    {
                        _rainSearchJob = null;
                        detail = "rain=owned_graph_not_for_points";
                        return false;
                    }
                    graph = ownedGraph;
                }
                if (RuntimeRainNavDerivedData.PrepareLinksForRoute(graph, capabilities))
                    _rainSearchJob = null;
                RainSearchJob job = _rainSearchJob;
                if (job == null || !job.Matches(graph, from, to))
                {
                    RAINPathFinder finder = graph.CreatePathFinder();
                    if (finder == null)
                    {
                        _rainSearchJob = null;
                        detail = "rain=no_finder graph=" + graph.GetType().Name;
                        return false;
                    }

                    finder.MaxYOffset = 4f;
                    finder.MaxPathfindingSteps = 32768;
                    finder.MaxPathLength = 1200f;
                    finder.StartPath(graph, from, to);
                    job = new RainSearchJob(graph, finder, from, to);
                    _rainSearchJob = job;
                }

                Stopwatch slice = Stopwatch.StartNew();
                RAINPath path;
                bool complete = job.Finder.ComputePath(out path);
                slice.Stop();
                job.Slices++;
                job.CpuMilliseconds += slice.ElapsedMilliseconds;

                if (!complete && job.Finder.InProgress)
                {
                    if (job.Slices >= 120 || job.CpuMilliseconds >= 1200L)
                    {
                        _rainSearchJob = null;
                        detail = "rain=timeout graph=" + graph.GetType().Name + " slices=" + job.Slices + " ms=" + job.CpuMilliseconds;
                        return false;
                    }

                    pending = true;
                    detail = "rain=pending graph=" + graph.GetType().Name + " slices=" + job.Slices + " sliceMs=" + slice.ElapsedMilliseconds + " ms=" + job.CpuMilliseconds;
                    return false;
                }

                _rainSearchJob = null;
                if (path == null || !path.IsValid || path.WaypointCount == 0)
                {
                    detail = "rain=no_path graph=" + graph.GetType().Name + " slices=" + job.Slices + " ms=" + job.CpuMilliseconds;
                    return false;
                }

                List<Vector3> linkedPath;
                List<bool> linkedFlags;
                offMesh = RuntimeRainNavDerivedData.TryBuildLinkedWorldPath(path, from, to,
                    out linkedPath, out linkedFlags);
                if (offMesh)
                {
                    result = linkedPath;
                    offMeshFlags = linkedFlags;
                }
                else
                {
                    result = new List<Vector3>(path.WaypointCount);
                    for (int i = 0; i < path.WaypointCount; i++)
                        result.Add(path.GetWaypointPosition(i));
                }
                partial = path.IsPartial;
                detail = "rain=ok graph=" + graph.GetType().Name + " pts=" + result.Count +
                    " partial=" + (path.IsPartial ? "1" : "0") + " offmesh=" + (offMesh ? "1" : "0") +
                    " slices=" + job.Slices + " ms=" + job.CpuMilliseconds;
                return result.Count > 0;
            }
            catch (Exception ex)
            {
                _rainSearchJob = null;
                detail = "rain=ex:" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 96);
                return false;
            }
        }

        private static bool ValidateRainPath(Vector3 from, List<Vector3> points,
            AutoBattleRouteCapabilities capabilities, Transform ignoreRoot,
            List<bool> forcedJumpFlags, out List<bool> jumpFlags, out string detail)
        {
            jumpFlags = new List<bool>(points == null ? 0 : points.Count);
            for (int i = 0; points != null && i < points.Count; i++) jumpFlags.Add(false);
            detail = "empty";
            if (points == null || points.Count == 0) return false;
            if (!IsFinite(from))
            {
                detail = "invalid_start";
                return false;
            }
            Vector3 previous = from;
            int jumps = 0;
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 target = points[i];
                if (!IsFinite(target))
                {
                    detail = "invalid_waypoint=" + i;
                    return false;
                }
                float horizontal = XZDistance(previous, target);
                if (horizontal < 0.12f)
                {
                    previous = target;
                    continue;
                }

                bool forcedJump = forcedJumpFlags != null && i < forcedJumpFlags.Count && forcedJumpFlags[i];
                if (forcedJump)
                {
                    bool validLink = capabilities != null && capabilities.AllowJump &&
                                     TryJumpSegment(previous, target, capabilities, ignoreRoot);
                    if (!validLink)
                    {
                        detail = "offmesh_invalid waypoint=" + i;
                        return false;
                    }
                    jumpFlags[i] = true;
                    jumps++;
                    previous = target;
                    continue;
                }

                int segments = Mathf.Clamp(Mathf.CeilToInt(horizontal / 0.9f), 1, 96);
                Vector3 segmentStart = previous;
                int blockedSegment = 0;
                for (int segment = 1; segment <= segments; segment++)
                {
                    Vector3 segmentEnd = Vector3.Lerp(previous, target, (float)segment / segments);
                    if (!CanFollowSegment(segmentStart, segmentEnd, ignoreRoot))
                    {
                        blockedSegment = segment;
                        break;
                    }
                    segmentStart = segmentEnd;
                }
                if (blockedSegment > 0)
                {
                    Vector3 jumpDirection = target - previous;
                    jumpDirection.y = 0f;
                    float rise = target.y - previous.y;
                    bool lowObstacle = ShouldJumpForwardObstacle(previous, jumpDirection, ignoreRoot);
                    bool jumpable = capabilities != null && capabilities.AllowJump &&
                                    (rise > 0.62f || lowObstacle) && horizontal <= 4.2f &&
                                    TryJumpSegment(previous, target, capabilities, ignoreRoot);
                    if (!jumpable)
                    {
                        detail = "blocked waypoint=" + i + " segment=" + blockedSegment + "/" + segments;
                        return false;
                    }
                    jumpFlags[i] = true;
                    jumps++;
                }
                previous = target;
            }
            detail = "ok jumps=" + jumps;
            return true;
        }

        private static List<Vector3> OptimizeRainPathWithHardLinks(Vector3 from, List<Vector3> points,
            List<bool> jumpFlags, Transform ignoreRoot, out List<bool> optimizedFlags, out string detail)
        {
            optimizedFlags = new List<bool>();
            List<Vector3> optimized = new List<Vector3>();
            if (points == null || points.Count == 0)
            {
                detail = "opt=offmesh_empty";
                return optimized;
            }

            Vector3 anchor = from;
            int index = 0;
            int removed = 0;
            while (index < points.Count)
            {
                int jumpIndex = -1;
                for (int i = index; i < points.Count; i++)
                {
                    if (jumpFlags != null && i < jumpFlags.Count && jumpFlags[i])
                    {
                        jumpIndex = i;
                        break;
                    }
                }
                int normalEnd = jumpIndex >= 0 ? jumpIndex - 1 : points.Count - 1;
                int cursor = index;
                while (cursor <= normalEnd)
                {
                    int chosen = cursor;
                    for (int candidate = normalEnd; candidate > cursor; candidate--)
                    {
                        if (!CanFollowSegment(anchor, points[candidate], ignoreRoot)) continue;
                        chosen = candidate;
                        break;
                    }
                    AddOptimizedPoint(optimized, optimizedFlags, points[chosen], false);
                    removed += chosen - cursor;
                    anchor = points[chosen];
                    cursor = chosen + 1;
                }
                if (jumpIndex < 0) break;
                AddOptimizedPoint(optimized, optimizedFlags, points[jumpIndex], true);
                anchor = points[jumpIndex];
                index = jumpIndex + 1;
            }
            detail = "opt=offmesh_hard_anchors in=" + points.Count + " out=" + optimized.Count +
                " removed=" + removed;
            return optimized;
        }

        private static void AddOptimizedPoint(List<Vector3> points, List<bool> flags, Vector3 point, bool jump)
        {
            if (points.Count > 0 && XZDistance(points[points.Count - 1], point) < 0.08f &&
                Mathf.Abs(points[points.Count - 1].y - point.y) < 0.10f)
            {
                if (jump) flags[flags.Count - 1] = true;
                return;
            }
            points.Add(point);
            flags.Add(jump);
        }

        private static object InvokePathBuilder(object graph, Vector3 from, Vector3 to, out string detail)
        {
            detail = "invoke=none";
            if (graph == null) return null;
            Type t = graph.GetType();
            string[] names = { "GetPath", "GetPathTo", "CalculatePath", "BuildPath" };
            for (int i = 0; i < names.Length; i++)
            {
                MethodInfo mi = t.GetMethod(names[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mi == null) continue;
                ParameterInfo[] ps = mi.GetParameters();
                if (ps.Length < 2 || ps[0].ParameterType != typeof(Vector3) || ps[1].ParameterType != typeof(Vector3)) continue;
                object[] args = new object[ps.Length];
                args[0] = from;
                args[1] = to;
                for (int k = 2; k < ps.Length; k++)
                    args[k] = ps[k].ParameterType.IsValueType ? Activator.CreateInstance(ps[k].ParameterType) : null;
                try
                {
                    object value = mi.Invoke(graph, args);
                    if (value != null)
                    {
                        detail = "invoke=" + names[i];
                        return value;
                    }
                }
                catch (Exception ex)
                {
                    detail = "invoke_ex=" + names[i] + ":" + ex.GetType().Name;
                }
            }
            if (detail == "invoke=none") detail = "invoke=no_matching_method";
            return null;
        }

        private static IList<Vector3> ExtractPathPoints(object pathObj)
        {
            if (pathObj == null) return null;
            IList<Vector3> direct = pathObj as IList<Vector3>;
            if (direct != null) return direct;

            string[] props = { "WaypointList", "Waypoints", "Corners", "Points" };
            for (int i = 0; i < props.Length; i++)
            {
                PropertyInfo pi = pathObj.GetType().GetProperty(props[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi == null) continue;
                try
                {
                    IList<Vector3> points = pi.GetValue(pathObj, null) as IList<Vector3>;
                    if (points != null) return points;
                }
                catch
                {
                }
            }
            return null;
        }

        private static void UpdateNavigationStatus(Vector3 probe)
        {
            bool runtimeRequested = RuntimeRainNavMesh.Requested;
            if ((!_navResourceDeclared || !_navLoadRequested) && !runtimeRequested)
            {
                if (_navState != AutoBattleNavResourceState.Unavailable)
                    SetNavigationState(AutoBattleNavResourceState.Unavailable, "map=" + SafeMap(_navMapName) + " reason=not_requested");
                return;
            }

            if (runtimeRequested && RuntimeRainNavMesh.HasFailed)
            {
                SetNavigationState(AutoBattleNavResourceState.Fallback,
                    "map=" + SafeMap(_navMapName) + " provider=runtime reason=" + RuntimeRainNavMesh.Detail);
                return;
            }
            if (runtimeRequested && RuntimeRainNavMesh.IsPending)
            {
                SetNavigationState(AutoBattleNavResourceState.Loading,
                    "map=" + SafeMap(_navMapName) + " provider=runtime " + RuntimeRainNavMesh.Detail);
                return;
            }

            float now = Time.realtimeSinceStartup;
            float interval = _navState == AutoBattleNavResourceState.Fallback ? 2.0f : 0.35f;
            if (now < _nextNavProbeTime) return;
            _nextNavProbeTime = now + interval;

            int graphCount = 0;
            bool active = false;
            bool nearest = false;
            int rainGraphCount = 0;
            bool rainNearest = false;
            string reason = string.Empty;
            try
            {
                active = AstarPath.active != null;
                Pathfinding.NavGraph[] graphs = active ? AstarPath.active.graphs : null;
                graphCount = graphs == null ? 0 : graphs.Length;
                if (active && graphCount > 0)
                    nearest = AstarPath.active.GetNearest(probe).node != null;
            }
            catch (Exception ex)
            {
                reason = ex.GetType().Name + ":" + SafeOneLine(ex.Message, 80);
            }

            try
            {
                NavigationManager manager = NavigationManager.Instance;
                rainGraphCount = manager == null || manager.NavMeshGraphs == null ? 0 : manager.NavMeshGraphs.Count;
                if (manager != null && rainGraphCount > 0)
                {
                    List<RAINNavigationGraph> rainGraphs = manager.GraphsForPoints(probe, probe, 4f, NavigationManager.GraphType.Navmesh, null);
                    rainNearest = rainGraphs != null && rainGraphs.Count > 0;
                    if (runtimeRequested)
                    {
                        RAINNavigationGraph ownedGraph = RuntimeRainNavMesh.OwnedGraph;
                        rainNearest = ownedGraph != null && rainGraphs != null && rainGraphs.Contains(ownedGraph);
                    }
                }
            }
            catch (Exception ex)
            {
                if (string.IsNullOrEmpty(reason)) reason = "rain=" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 80);
            }

            if (rainGraphCount > 0 && rainNearest)
            {
                SetNavigationState(AutoBattleNavResourceState.Ready,
                    "map=" + SafeMap(_navMapName) +
                    " provider=rain" +
                    " astarActive=" + (active ? "1" : "0") + " astarGraphs=" + graphCount + " astarNearest=" + (nearest ? "1" : "0") +
                    " rainGraphs=" + rainGraphCount + " rainNearest=" + (rainNearest ? "1" : "0") +
                    " wait=" + (now - _navLoadStartedAt).ToString("0.0"));
                return;
            }

            float timeoutOrigin = runtimeRequested && RuntimeRainNavMesh.IsReady
                ? RuntimeRainNavMesh.ReadyAt
                : _navLoadStartedAt;
            if (now - timeoutOrigin >= NavLoadTimeout)
            {
                SetNavigationState(AutoBattleNavResourceState.Fallback,
                    "map=" + SafeMap(_navMapName) +
                    " astarActive=" + (active ? "1" : "0") + " astarGraphs=" + graphCount + " astarNearest=" + (nearest ? "1" : "0") +
                    " rainGraphs=" + rainGraphCount + " rainNearest=" + (rainNearest ? "1" : "0") +
                    " wait=" + (now - _navLoadStartedAt).ToString("0.0") + (string.IsNullOrEmpty(reason) ? string.Empty : " reason=" + reason));
                return;
            }

            if (now >= _nextNavLogTime)
            {
                _nextNavLogTime = now + 1.0f;
                FileLogger.Log("AUTO-BATTLE][NAVMESH",
                    "state=loading map=" + SafeMap(_navMapName) +
                    " astarActive=" + (active ? "1" : "0") + " astarGraphs=" + graphCount + " astarNearest=" + (nearest ? "1" : "0") +
                    " rainGraphs=" + rainGraphCount + " rainNearest=" + (rainNearest ? "1" : "0") +
                    " wait=" + (now - _navLoadStartedAt).ToString("0.0"));
            }
        }

        private static void SetNavigationState(AutoBattleNavResourceState state, string detail)
        {
            bool changed = _navState != state;
            _navState = state;
            if (changed || Time.realtimeSinceStartup >= _nextNavLogTime)
            {
                _nextNavLogTime = Time.realtimeSinceStartup + 1.0f;
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "state=" + state.ToString().ToLowerInvariant() + " " + detail);
            }
        }

        private static string SafeMap(string mapName)
        {
            return string.IsNullOrEmpty(mapName) ? "-" : SafeOneLine(mapName, 48);
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                   !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                   !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private static AutoBattleRouteResult FromPoints(string provider, bool partial, List<Vector3> points, string detail)
        {
            AutoBattleRouteResult r = new AutoBattleRouteResult();
            r.Success = points != null && points.Count > 0;
            r.Partial = partial;
            r.Provider = provider;
            r.Detail = "provider=" + provider + " " + detail;
            if (points != null)
            {
                for (int i = 0; i < points.Count && r.Corners.Count < 48; i++)
                {
                    r.Corners.Add(points[i]);
                    r.JumpFlags.Add(false);
                }
            }
            return r;
        }

        private static AutoBattleRouteResult FromSteps(string provider, bool partial, List<RouteStep> steps, string detail)
        {
            AutoBattleRouteResult r = new AutoBattleRouteResult();
            r.Success = steps != null && steps.Count > 0;
            r.Partial = partial;
            r.Provider = provider;
            r.Detail = "provider=" + provider + " " + detail;
            if (steps != null)
            {
                for (int i = 0; i < steps.Count && r.Corners.Count < 48; i++)
                {
                    r.Corners.Add(steps[i].Pos);
                    r.JumpFlags.Add(steps[i].Jump);
                }
            }
            return r;
        }

        private static AutoBattleRouteResult Fail(string provider, string detail)
        {
            AutoBattleRouteResult r = new AutoBattleRouteResult();
            r.Success = false;
            r.Partial = false;
            r.Provider = provider;
            r.Detail = "provider=" + provider + " " + detail;
            return r;
        }

        private static AutoBattleRouteResult Pending(string provider, string detail)
        {
            AutoBattleRouteResult r = new AutoBattleRouteResult();
            r.Success = false;
            r.Partial = true;
            r.Provider = provider;
            r.Detail = "provider=" + provider + " " + detail;
            return r;
        }

        private static List<RouteStep> Reconstruct(GridNode end)
        {
            List<RouteStep> reversed = new List<RouteStep>(32);
            GridNode n = end;
            while (n != null)
            {
                reversed.Add(new RouteStep(n.Pos, n.JumpFromParent));
                n = n.Parent;
            }
            reversed.Reverse();
            if (reversed.Count > 0) reversed[0] = new RouteStep(reversed[0].Pos, false);
            return reversed;
        }

        private static List<RouteStep> SmoothPath(List<RouteStep> raw, Transform ignoreRoot)
        {
            List<RouteStep> smooth = new List<RouteStep>(48);
            if (raw == null || raw.Count == 0) return smooth;
            if (raw.Count == 1)
            {
                smooth.Add(raw[0]);
                return smooth;
            }

            int index = 0;
            while (index < raw.Count - 1 && smooth.Count < 48)
            {
                int best = index + 1;
                for (int j = raw.Count - 1; j > index + 1; j--)
                {
                    if (ContainsJumpStep(raw, index + 1, j)) continue;
                    if (CanFollowSegment(raw[index].Pos, raw[j].Pos, ignoreRoot))
                    {
                        best = j;
                        break;
                    }
                }
                smooth.Add(raw[best]);
                index = best;
            }
            return smooth;
        }

        private static bool ContainsJumpStep(List<RouteStep> steps, int from, int to)
        {
            for (int i = from; i <= to && i < steps.Count; i++)
            {
                if (steps[i].Jump) return true;
            }
            return false;
        }

        private static int CountJumpSteps(List<RouteStep> steps)
        {
            int count = 0;
            if (steps == null) return count;
            for (int i = 0; i < steps.Count; i++)
                if (steps[i].Jump) count++;
            return count;
        }

        private static int HeightLayer(float y)
        {
            return Mathf.RoundToInt(y / HeightLayerSize);
        }

        private static void CollectGroundLayersCached(int x, int z, Vector3 raw, float referenceY,
            PhysicsSearchJob job, Transform ignoreRoot)
        {
            GroundSampleKey key = new GroundSampleKey(x, z, Mathf.RoundToInt(referenceY * 4f));
            Vector3[] cached;
            job.Grounds.Clear();
            if (job.GroundSamples.TryGetValue(key, out cached))
            {
                job.Stats.GroundCacheHits++;
                if (cached != null)
                {
                    for (int i = 0; i < cached.Length; i++) job.Grounds.Add(cached[i]);
                }
                return;
            }

            job.Stats.GroundCacheMisses++;
            CollectGroundLayers(raw, referenceY, job.Capabilities, job.Grounds, ignoreRoot);
            cached = job.Grounds.Count == 0 ? new Vector3[0] : job.Grounds.ToArray();
            job.GroundSamples[key] = cached;
        }

        private static void GetWalkSample(GridKey fromKey, GridKey toKey, Vector3 from, Vector3 to,
            PhysicsSearchJob job, Transform ignoreRoot, out bool walkClear, out bool groundSupported)
        {
            WalkSampleKey key = new WalkSampleKey(fromKey, toKey, true);
            byte value;
            if (job.WalkSamples.TryGetValue(key, out value))
            {
                job.Stats.WalkCacheHits++;
                walkClear = (value & 1) != 0;
                groundSupported = (value & 2) != 0;
                return;
            }

            job.Stats.WalkCacheMisses++;
            walkClear = HasWalkSegment(from, to, ignoreRoot) &&
                        HasNavigationBodyClearance(from, to, ignoreRoot, NavigationBodyRadius, false);
            groundSupported = HasGroundSupportSegment(from, to, ignoreRoot);
            value = (byte)((walkClear ? 1 : 0) | (groundSupported ? 2 : 0));
            job.WalkSamples[key] = value;
        }

        private static bool GetJumpSample(GridKey fromKey, GridKey toKey, Vector3 from, Vector3 to,
            PhysicsSearchJob job, Transform ignoreRoot)
        {
            WalkSampleKey key = new WalkSampleKey(fromKey, toKey, false);
            bool value;
            if (job.JumpSamples.TryGetValue(key, out value))
            {
                job.Stats.JumpCacheHits++;
                return value;
            }

            job.Stats.JumpCacheMisses++;
            value = TryJumpSegment(from, to, job.Capabilities, ignoreRoot);
            job.JumpSamples[key] = value;
            return value;
        }

        private static void CollectGroundLayers(Vector3 raw, float referenceY, AutoBattleRouteCapabilities capabilities,
            List<Vector3> result, Transform ignoreRoot)
        {
            result.Clear();
            try
            {
                float maxRise = Mathf.Max(MaxStepHeight, capabilities.JumpHeight * 0.92f);
                float maxDrop = Mathf.Max(8.0f, capabilities.JumpHeight + 2.5f);
                float rayUp = maxRise + 1.5f;
                Vector3 origin = new Vector3(raw.x, referenceY + rayUp, raw.z);
                RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, rayUp + maxDrop, GroundMask);
                if (hits == null || hits.Length == 0) return;

                for (int i = 0; i < hits.Length; i++)
                {
                    Vector3 p = hits[i].point;
                    float rise = p.y - referenceY;
                    if (rise > maxRise + 0.08f || rise < -maxDrop) continue;
                    if (!HasStandingSpace(p, ignoreRoot)) continue;

                    bool duplicate = false;
                    for (int k = 0; k < result.Count; k++)
                    {
                        if (Mathf.Abs(result[k].y - p.y) < 0.28f)
                        {
                            duplicate = true;
                            break;
                        }
                    }
                    if (duplicate) continue;

                    int insert = result.Count;
                    float delta = Mathf.Abs(p.y - referenceY);
                    for (int k = 0; k < result.Count; k++)
                    {
                        if (delta < Mathf.Abs(result[k].y - referenceY))
                        {
                            insert = k;
                            break;
                        }
                    }
                    result.Insert(insert, p);
                    if (result.Count > 5) result.RemoveAt(result.Count - 1);
                }
            }
            catch
            {
                result.Clear();
            }
        }

        private static bool HasStandingSpace(Vector3 ground, Transform ignoreRoot)
        {
            try
            {
                Vector3 origin = ground + Vector3.up * 0.12f;
                RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.up, 1.62f, BlockMask);
                return !HasNonIgnoredHit(hits, ignoreRoot);
            }
            catch
            {
                return true;
            }
        }

        private static bool HasGroundSupportSegment(Vector3 from, Vector3 to, Transform ignoreRoot)
        {
            float distance = XZDistance(from, to);
            if (distance < 0.65f) return true;
            int samples = Mathf.Clamp(Mathf.CeilToInt(distance / 0.55f), 2, 160);
            try
            {
                for (int i = 1; i < samples; i++)
                {
                    float t = (float)i / samples;
                    Vector3 expected = Vector3.Lerp(from, to, t);
                    Vector3 origin = expected + Vector3.up * 0.9f;
                    RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 1.9f, GroundMask);
                    bool supported = false;
                    if (hits != null)
                    {
                        for (int h = 0; h < hits.Length; h++)
                        {
                            if (IsIgnored(hits[h].collider == null ? null : hits[h].collider.transform, ignoreRoot)) continue;
                            if (Mathf.Abs(hits[h].point.y - expected.y) <= 0.65f)
                            {
                                supported = true;
                                break;
                            }
                        }
                    }
                    if (!supported) return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryJumpSegment(Vector3 from, Vector3 to, AutoBattleRouteCapabilities capabilities, Transform ignoreRoot)
        {
            float horizontal = XZDistance(from, to);
            float rise = to.y - from.y;
            float jumpHeight = Mathf.Max(1.2f, capabilities.JumpHeight);
            float jumpVelocity = capabilities.JumpVelocity > 0.1f ? capabilities.JumpVelocity : Mathf.Sqrt(jumpHeight * 39.2f);
            float runSpeed = capabilities.RunSpeed > 0.1f ? capabilities.RunSpeed : 6.0f;
            float maxHorizontal = Mathf.Clamp(runSpeed * (2.0f * jumpVelocity / 19.6f) * 0.65f, 2.2f, 4.2f);
            if (horizontal < 0.35f || horizontal > maxHorizontal) return false;
            if (rise > jumpHeight * 0.92f || rise < -Mathf.Max(8.0f, jumpHeight + 2.0f)) return false;
            if (!HasStandingSpace(to, ignoreRoot)) return false;

            Vector3 previous = from;
            float arc = Mathf.Max(0.72f, jumpHeight - Mathf.Max(0f, rise) * 0.35f);
            const int samples = 7;
            for (int i = 1; i <= samples; i++)
            {
                float t = (float)i / samples;
                Vector3 next = Vector3.Lerp(from, to, t);
                next.y += 4.0f * arc * t * (1.0f - t);
                Vector3 segment = next - previous;
                float length = segment.magnitude;
                if (length > 0.01f)
                {
                    Vector3 dir = segment / length;
                    for (int h = 0; h < JumpProbeHeights.Length; h++)
                    {
                        RaycastHit[] hits = Physics.RaycastAll(previous + Vector3.up * JumpProbeHeights[h], dir, length, BlockMask);
                        if (HasNonIgnoredHit(hits, ignoreRoot)) return false;
                    }
                }
                previous = next;
            }
            return true;
        }

        private static bool TrySnapToGroundNear(Vector3 raw, float referenceY, float maxDelta, out Vector3 grounded, bool allowRaw)
        {
            grounded = raw;
            try
            {
                float rayUp = Mathf.Max(GroundRayUp, maxDelta + 1.0f);
                Vector3 origin = new Vector3(raw.x, referenceY + rayUp, raw.z);
                RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, rayUp + GroundRayDown, GroundMask);
                bool found = false;
                float bestDelta = maxDelta;
                if (hits != null)
                {
                    for (int i = 0; i < hits.Length; i++)
                    {
                        float delta = Mathf.Abs(hits[i].point.y - referenceY);
                        if (delta <= bestDelta)
                        {
                            bestDelta = delta;
                            grounded = hits[i].point;
                            found = true;
                        }
                    }
                }
                if (found) return true;
            }
            catch
            {
            }
            if (allowRaw)
            {
                grounded = raw;
                return true;
            }
            return false;
        }

        private static bool TrySnapToGround(Vector3 raw, out Vector3 grounded, bool allowRaw)
        {
            grounded = raw;
            try
            {
                Vector3 origin = raw + Vector3.up * GroundRayUp;
                RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, GroundRayUp + GroundRayDown, GroundMask);
                if (hits != null && hits.Length > 0)
                {
                    bool found = false;
                    Vector3 best = raw;
                    float bestDelta = MaxStepHeight + 0.35f;
                    for (int i = 0; i < hits.Length; i++)
                    {
                        float delta = Mathf.Abs(hits[i].point.y - raw.y);
                        if (delta <= bestDelta)
                        {
                            bestDelta = delta;
                            best = hits[i].point;
                            found = true;
                        }
                    }
                    if (found)
                    {
                        grounded = best;
                        return true;
                    }
                }
            }
            catch
            {
            }
            if (allowRaw)
            {
                grounded = raw;
                return true;
            }
            return false;
        }

        private static bool HasNonIgnoredHit(RaycastHit[] hits, Transform ignoreRoot)
        {
            if (hits == null || hits.Length == 0) return false;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider c = hits[i].collider;
                if (c == null) continue;
                Transform t = c.transform;
                if (IsIgnored(t, ignoreRoot)) continue;
                return true;
            }
            return false;
        }

        private static bool IsIgnored(Transform hit, Transform ignoreRoot)
        {
            if (hit == null || ignoreRoot == null) return false;
            try
            {
                return hit == ignoreRoot || hit.IsChildOf(ignoreRoot);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsBetterFrontier(GridNode candidate, GridNode current, Vector3 goal)
        {
            if (candidate == null) return false;
            if (current == null) return true;
            float cd = XZDistance(candidate.Pos, goal) + Mathf.Abs(candidate.Pos.y - goal.y) * 4.0f;
            float bd = XZDistance(current.Pos, goal) + Mathf.Abs(current.Pos.y - goal.y) * 4.0f;
            if (cd < bd - 0.15f) return true;
            if (Mathf.Abs(cd - bd) < 0.15f && candidate.G < current.G) return true;
            return false;
        }

        private static int PopBestIndex(List<GridNode> open)
        {
            int best = 0;
            float bestF = open[0].G + open[0].H;
            for (int i = 1; i < open.Count; i++)
            {
                float f = open[i].G + open[i].H;
                if (f < bestF)
                {
                    best = i;
                    bestF = f;
                }
            }
            return best;
        }

        private static int WorldToGridX(Vector3 p, Vector3 origin)
        {
            return Mathf.RoundToInt((p.x - origin.x) / CellSize);
        }

        private static int WorldToGridZ(Vector3 p, Vector3 origin)
        {
            return Mathf.RoundToInt((p.z - origin.z) / CellSize);
        }

        private static float Heuristic(int x, int z, int gx, int gz)
        {
            int dx = x - gx;
            int dz = z - gz;
            return Mathf.Sqrt(dx * dx + dz * dz) * CellSize;
        }

        private static float Heuristic(Vector3 from, Vector3 goal)
        {
            float horizontal = XZDistance(from, goal);
            float vertical = Mathf.Abs(from.y - goal.y);
            return horizontal + vertical * 1.35f;
        }

        private static float XZDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static long Key(int x, int z)
        {
            return ((long)x << 32) ^ (long)(uint)z;
        }

        private static int GroundMask
        {
            get
            {
                if (_groundMask == int.MinValue)
                {
                    _groundMask = LayerMask.GetMask(new string[] { "Terrarin" });
                    if (_groundMask == 0) _groundMask = 256;
                }
                return _groundMask;
            }
        }

        private static int BlockMask
        {
            get
            {
                if (_blockMask == int.MinValue)
                {
                    // Global planning only uses static map geometry. Dynamic actors are handled by the follower.
                    _blockMask = LayerMask.GetMask(new string[] { "Terrarin" });
                    if (_blockMask == 0) _blockMask = 256;
                }
                return _blockMask;
            }
        }

        private static Type FindType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    Type t = assemblies[i].GetType(fullName, false);
                    if (t != null) return t;
                }
                catch
                {
                }
            }
            return null;
        }

        private static Vector3[] ReadCorners(object pathObj)
        {
            if (pathObj == null) return null;
            try
            {
                PropertyInfo pi = pathObj.GetType().GetProperty("corners", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi == null) pi = pathObj.GetType().GetProperty("Corners", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi == null) return null;
                return pi.GetValue(pathObj, null) as Vector3[];
            }
            catch
            {
                return null;
            }
        }

        private static string ReadStatus(object pathObj)
        {
            if (pathObj == null) return "-";
            try
            {
                PropertyInfo pi = pathObj.GetType().GetProperty("status", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi == null) pi = pathObj.GetType().GetProperty("Status", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi == null) return "-";
                object v = pi.GetValue(pathObj, null);
                return v == null ? "-" : v.ToString();
            }
            catch
            {
                return "-";
            }
        }

        private static void LogRoute(AutoBattleRouteResult route)
        {
            try
            {
                if (Time.time < _nextRouteLogTime) return;
                _nextRouteLogTime = Time.time + 0.85f;
                FileLogger.Log("AUTO-BATTLE][ROUTE", route == null ? "provider=none result=fail reason=null_route" : route.Detail);
            }
            catch
            {
            }
        }

        private static string SafeOneLine(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "-";
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            if (s.Length > max) s = s.Substring(0, max);
            return s;
        }

        private struct RouteStep
        {
            public Vector3 Pos;
            public bool Jump;

            public RouteStep(Vector3 pos, bool jump)
            {
                Pos = pos;
                Jump = jump;
            }
        }

        private sealed class PhysicsSearchJob
        {
            public Vector3 QueryFrom;
            public Vector3 QueryTo;
            public Transform IgnoreRoot;
            public AutoBattleRouteCapabilities Capabilities;
            public string UnityDetail;
            public string AstarDetail;
            public string RainDetail;
            public Vector3 Start;
            public Vector3 Goal;
            public int MaxGrid;
            public int GoalX;
            public int GoalZ;
            public readonly Dictionary<GridKey, GridNode> All = new Dictionary<GridKey, GridNode>(768);
            public readonly Dictionary<long, int> LayerCounts = new Dictionary<long, int>(512);
            public readonly MinNodeHeap Open = new MinNodeHeap(384);
            public readonly HashSet<int> HeightLayers = new HashSet<int>();
            public readonly List<Vector3> Grounds = new List<Vector3>(5);
            public readonly Dictionary<GroundSampleKey, Vector3[]> GroundSamples = new Dictionary<GroundSampleKey, Vector3[]>(512);
            public readonly Dictionary<WalkSampleKey, byte> WalkSamples = new Dictionary<WalkSampleKey, byte>(1024);
            public readonly Dictionary<WalkSampleKey, bool> JumpSamples = new Dictionary<WalkSampleKey, bool>(512);
            public PhysStats Stats;
            public GridNode First;
            public GridNode Best;
            public GridNode Found;
            public int Expanded;
            public int JumpLinks;
            public int Slices;
            public int BudgetSkips;
            public long LastSliceMilliseconds;
            public long CpuMilliseconds;
            public float LastSliceBudgetMilliseconds;
            public int LastSliceNodes;
            public float LastFrameMilliseconds;
            public float LastFrameEma;

            public bool Matches(Vector3 from, Vector3 to, Transform ignoreRoot)
            {
                return IgnoreRoot == ignoreRoot &&
                       XZDistance(QueryFrom, from) <= 3.25f &&
                       Mathf.Abs(QueryFrom.y - from.y) <= 1.5f &&
                       XZDistance(QueryTo, to) <= 0.45f &&
                       Mathf.Abs(QueryTo.y - to.y) <= 0.50f;
            }
        }

        private sealed class RainSearchJob
        {
            public readonly RAINNavigationGraph Graph;
            public readonly RAINPathFinder Finder;
            public readonly Vector3 From;
            public readonly Vector3 To;
            public int Slices;
            public long CpuMilliseconds;

            public RainSearchJob(RAINNavigationGraph graph, RAINPathFinder finder, Vector3 from, Vector3 to)
            {
                Graph = graph;
                Finder = finder;
                From = from;
                To = to;
            }

            public bool Matches(RAINNavigationGraph graph, Vector3 from, Vector3 to)
            {
                return Graph == graph &&
                       XZDistance(From, from) <= 0.80f && Mathf.Abs(From.y - from.y) <= 0.80f &&
                       XZDistance(To, to) <= 0.65f && Mathf.Abs(To.y - to.y) <= 0.75f;
            }
        }

        private struct GroundSampleKey : IEquatable<GroundSampleKey>
        {
            private readonly int _x;
            private readonly int _z;
            private readonly int _referenceLayer;

            public GroundSampleKey(int x, int z, int referenceLayer)
            {
                _x = x;
                _z = z;
                _referenceLayer = referenceLayer;
            }

            public bool Equals(GroundSampleKey other)
            {
                return _x == other._x && _z == other._z && _referenceLayer == other._referenceLayer;
            }

            public override bool Equals(object obj)
            {
                return obj is GroundSampleKey && Equals((GroundSampleKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _x;
                    hash = hash * 397 ^ _z;
                    hash = hash * 397 ^ _referenceLayer;
                    return hash;
                }
            }
        }

        private struct WalkSampleKey : IEquatable<WalkSampleKey>
        {
            private readonly GridKey _from;
            private readonly GridKey _to;

            public WalkSampleKey(GridKey from, GridKey to, bool symmetric)
            {
                if (symmetric && CompareGridKey(from, to) > 0)
                {
                    _from = to;
                    _to = from;
                }
                else
                {
                    _from = from;
                    _to = to;
                }
            }

            public bool Equals(WalkSampleKey other)
            {
                return _from.Equals(other._from) && _to.Equals(other._to);
            }

            public override bool Equals(object obj)
            {
                return obj is WalkSampleKey && Equals((WalkSampleKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked { return _from.GetHashCode() * 397 ^ _to.GetHashCode(); }
            }
        }

        private static int CompareGridKey(GridKey a, GridKey b)
        {
            if (a.X != b.X) return a.X.CompareTo(b.X);
            if (a.Z != b.Z) return a.Z.CompareTo(b.Z);
            return a.Layer.CompareTo(b.Layer);
        }

        private struct GridKey : IEquatable<GridKey>
        {
            private readonly int _x;
            private readonly int _z;
            private readonly int _layer;

            public int X { get { return _x; } }
            public int Z { get { return _z; } }
            public int Layer { get { return _layer; } }

            public GridKey(int x, int z, int layer)
            {
                _x = x;
                _z = z;
                _layer = layer;
            }

            public bool Equals(GridKey other)
            {
                return _x == other._x && _z == other._z && _layer == other._layer;
            }

            public override bool Equals(object obj)
            {
                return obj is GridKey && Equals((GridKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _x;
                    hash = hash * 397 ^ _z;
                    hash = hash * 397 ^ _layer;
                    return hash;
                }
            }
        }

        private sealed class MinNodeHeap
        {
            private readonly List<GridNode> _items;

            public MinNodeHeap(int capacity)
            {
                _items = new List<GridNode>(capacity);
            }

            public int Count
            {
                get { return _items.Count; }
            }

            public void Push(GridNode node)
            {
                int index = _items.Count;
                _items.Add(node);
                while (index > 0)
                {
                    int parent = (index - 1) / 2;
                    if (!Less(node, _items[parent])) break;
                    _items[index] = _items[parent];
                    index = parent;
                }
                _items[index] = node;
            }

            public GridNode Pop()
            {
                if (_items.Count == 0) return null;
                GridNode root = _items[0];
                int lastIndex = _items.Count - 1;
                GridNode last = _items[lastIndex];
                _items.RemoveAt(lastIndex);
                if (_items.Count == 0) return root;

                int index = 0;
                while (true)
                {
                    int left = index * 2 + 1;
                    if (left >= _items.Count) break;
                    int right = left + 1;
                    int child = right < _items.Count && Less(_items[right], _items[left]) ? right : left;
                    if (!Less(_items[child], last)) break;
                    _items[index] = _items[child];
                    index = child;
                }
                _items[index] = last;
                return root;
            }

            private static bool Less(GridNode a, GridNode b)
            {
                float af = a.G + a.H;
                float bf = b.G + b.H;
                if (af < bf - 0.001f) return true;
                if (Mathf.Abs(af - bf) <= 0.001f) return a.H < b.H;
                return false;
            }
        }

        private sealed class GridNode
        {
            public int X;
            public int Z;
            public int Layer;
            public GridKey Key;
            public Vector3 Pos;
            public float G;
            public float H;
            public bool Closed;
            public bool JumpFromParent;
            public GridNode Parent;
        }

        private sealed class PhysStats
        {
            public int RejectGround;
            public int RejectBlock;
            public int GroundCacheHits;
            public int GroundCacheMisses;
            public int WalkCacheHits;
            public int WalkCacheMisses;
            public int JumpCacheHits;
            public int JumpCacheMisses;
        }
    }
}

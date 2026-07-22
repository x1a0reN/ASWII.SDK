using ASWDEBUG.Logger;
using ASWDEBUG.Cheats.SurvivalBot;
using ASWDEBUG.Cheats.AutoBattle.CompactNav;
using RAIN.Navigation;
using RAIN.Navigation.Graph;
using RAIN.Navigation.NavMesh;
using RAIN.Navigation.Pathfinding;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
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
        private const float WalkGradeSlack = 0.22f;
        private const float SamePositionTolerance = 0.12f;
        private const float SamePositionVerticalTolerance = 0.28f;
        private const float RainCornerClearanceRadius = 0.62f;
        private const float NavigationBodyRadius = 0.48f;
        private const float RainShortcutCorridorRadius = 0.28f;
        private const float RainDetourCellSize = 0.75f;
        private const int RainDetourRadiusCells = 12;
        private const int RainDetourMaxExpanded = 720;
        private const int RainPathStepsPerSlice = 2048;
        private const float RainStartLayerTolerance = 1.65f;
        private const float RainStartAnchorMaxRadius = 5.25f;

        private static readonly int[] Dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] Dz = { 0, 0, 1, -1, 1, -1, 1, -1 };
        private static readonly float[] MoveCost = { 1f, 1f, 1f, 1f, 1.4142f, 1.4142f, 1.4142f, 1.4142f };
        private static readonly float[] WalkProbeHeights = { 0.35f, 0.9f, 1.45f };
        private static readonly float[] JumpProbeHeights = { 0.32f, 1.12f };
        private static readonly float[] NavigationProbeHeights = { 0.38f, 0.92f, 1.42f };
        private static readonly float[] NavigationProbeOffsetScales = { -1f, 0f, 1f };
        private static readonly float[] JumpProbeSideOffsets = { -0.42f, 0f, 0.42f };

        private static int _groundMask = int.MinValue;
        private static int _blockMask = int.MinValue;

        private static string _manifestText;
        private static bool _manifestRead;
        private static string _navMapName = string.Empty;
        private static bool _navResourceDeclared;
        private static bool _navLoadRequested;
        private static bool _compactNavigationRequested;
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
        private static readonly HashSet<string> RainFailureDumps = new HashSet<string>();

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
            if (_compactNavigationRequested)
                return CompactRainNavRuntime.IsPointOnGraph(point, tolerance);
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
            if (!_compactNavigationRequested)
                RuntimeRainNavMesh.Tick(level, player, navigationActive);
            if (player != null && player.transform != null)
                UpdateNavigationStatus(player.transform.position);
        }

        internal static void ShutdownNavigation(string reason)
        {
            _physicsSearchJob = null;
            _rainSearchJob = null;
            bool runtimeRainActive = !_compactNavigationRequested && !string.IsNullOrEmpty(_navMapName);
            CompactRainNavRuntime.Shutdown(reason);
            if (runtimeRainActive) RuntimeRainNavMesh.Shutdown(reason);
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

        private static void DeactivateNavigation(string reason, bool sceneExit)
        {
            _physicsSearchJob = null;
            _rainSearchJob = null;
            if (_compactNavigationRequested)
                CompactRainNavRuntime.DeactivateScene(reason);
            else if (!string.IsNullOrEmpty(_navMapName))
            {
                if (sceneExit) RuntimeRainNavMesh.SuspendForSceneExit(reason);
                else RuntimeRainNavMesh.Deactivate(reason);
            }
            ResetNavigationState();
        }

        private static void ResetNavigationState()
        {
            _navMapName = string.Empty;
            _navResourceDeclared = false;
            _navLoadRequested = false;
            _compactNavigationRequested = false;
            _navState = AutoBattleNavResourceState.Unavailable;
        }

        internal static void PrepareNavigationLoad(string mapName, ref bool loadNavmesh)
        {
            string normalized = (mapName ?? string.Empty).Trim().ToLowerInvariant();
            bool original = loadNavmesh;
            bool declared = ManifestDeclaresNavMesh(normalized);
            bool bakeMode = SurvivalBotManager.MapBakeEnabled;
            bool level33Test = SurvivalBotManager.Level33TestEnabled;
            bool residentLevel33 = string.Equals(normalized, "level33", StringComparison.OrdinalIgnoreCase);
            bool compactLevel33 = residentLevel33 && !bakeMode;

            if (compactLevel33)
                loadNavmesh = false;
            else if (declared && !loadNavmesh)
                loadNavmesh = true;

            bool runtimeRainRequired = !compactLevel33 && (bakeMode || level33Test || !declared);
            bool highDetailRain = runtimeRainRequired;
            bool compactReady = false;
            _compactNavigationRequested = compactLevel33;
            if (compactLevel33)
                compactReady = CompactRainNavRuntime.PrepareMap(normalized);
            else
            {
                CompactRainNavRuntime.DeactivateScene("prepare_noncompact:" + normalized);
                RuntimeRainNavMesh.PrepareMap(normalized, runtimeRainRequired, highDetailRain);
            }

            _navMapName = normalized;
            _navResourceDeclared = declared;
            _navLoadRequested = compactLevel33 || loadNavmesh || RuntimeRainNavMesh.Requested;
            _navLoadStartedAt = Time.realtimeSinceStartup;
            _nextNavProbeTime = 0f;
            _physicsSearchJob = null;
            _rainSearchJob = null;
            AutoBattleNavResourceState initialState = compactLevel33
                ? (compactReady ? AutoBattleNavResourceState.Ready : AutoBattleNavResourceState.Fallback)
                : (_navLoadRequested ? AutoBattleNavResourceState.Loading : AutoBattleNavResourceState.Unavailable);
            SetNavigationState(initialState,
                "map=" + SafeMap(normalized) +
                " manifest=" + (declared ? "hit" : "miss") +
                " original=" + (original ? "1" : "0") +
                " native=" + (loadNavmesh ? "1" : "0") +
                " compact=" + (compactLevel33 ? "1" : "0") +
                " runtime=" + (RuntimeRainNavMesh.Requested ? "1" : "0") +
                " bake=" + (bakeMode ? "1" : "0") +
                " forced=" + (!original && loadNavmesh ? "1" : "0") +
                (compactLevel33 ? " detail=" + CompactRainNavRuntime.Detail : string.Empty));
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
            bool highDetail = true;
            if (RuntimeRainNavMesh.IsBuilding && RuntimeRainNavMesh.IsHighDetail == highDetail &&
                string.Equals(RuntimeRainNavMesh.CurrentMapName, normalized,
                    StringComparison.OrdinalIgnoreCase)) return;
            if (RuntimeRainNavMesh.Requested && RuntimeRainNavMesh.IsHighDetail == highDetail &&
                string.Equals(RuntimeRainNavMesh.CurrentMapName, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            CompactRainNavRuntime.DeactivateScene("map_bake:" + normalized);
            _compactNavigationRequested = false;
            RuntimeRainNavMesh.PrepareMap(normalized, true, highDetail);
            _navMapName = normalized;
            _navResourceDeclared = ManifestDeclaresNavMesh(normalized);
            _navLoadRequested = true;
            _navLoadStartedAt = Time.realtimeSinceStartup;
            _nextNavProbeTime = 0f;
            _physicsSearchJob = null;
            _rainSearchJob = null;
            SetNavigationState(AutoBattleNavResourceState.Loading,
                "map=" + SafeMap(normalized) + " provider=runtime profile=" +
                (highDetail ? "max_detail" : "long_run_0.20") + " source=map_bake");
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
            string navigationProvider = _compactNavigationRequested ? "aswnav_0_10" : "rain_navmesh";
            string rainDetail = IsGameNavigationReady ? "ready" :
                (_compactNavigationRequested ? CompactRainNavRuntime.Detail : RuntimeRainNavMesh.Detail);

            if (IsGameNavigationReady)
            {
                bool rainPending;
                bool rainPartial;
                bool rainOffMesh;
                List<bool> rainOffMeshFlags;
                if (TryBuildRainPath(from, to, capabilities, ignoreRoot, out points, out rainDetail,
                    out rainPending, out rainPartial, out rainOffMesh, out rainOffMeshFlags))
                {
                    string optimizeDetail;
                    bool optimizerPartial = false;
                    if (rainOffMesh)
                    {
                        List<bool> optimizedFlags;
                        points = OptimizeRainPathWithHardLinks(from, points, rainOffMeshFlags,
                            capabilities, ignoreRoot, out optimizedFlags, out optimizerPartial,
                            out optimizeDetail);
                        rainOffMeshFlags = optimizedFlags;
                    }
                    else
                    {
                        points = OptimizeRainPath(from, points, capabilities, ignoreRoot,
                            out optimizerPartial, out optimizeDetail);
                    }
                    rainDetail += " " + optimizeDetail;
                    string validationDetail = "not_checked";
                    List<bool> validatedJumpFlags = new List<bool>();
                    bool physicsValidated = !rainPartial && points != null && points.Count > 0 &&
                        ValidateRainPath(from, points, capabilities,
                        ignoreRoot, rainOffMeshFlags, out validatedJumpFlags, out validationDetail);
                    if (physicsValidated)
                    {
                        _physicsSearchJob = null;
                        route = FromPoints(navigationProvider, optimizerPartial, points,
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
                    route = Pending(navigationProvider + "_pending", rainDetail);
                    LogRoute(route);
                    return route;
                }

                _physicsSearchJob = null;
                route = Fail(navigationProvider + "_required",
                    "result=fail reason=complete_rain_path_unavailable " + rainDetail);
                LogRoute(route);
                return route;
            }
            else
            {
                _physicsSearchJob = null;
                route = Pending(navigationProvider + "_pending",
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
                int walkBlockedSegment;
                int walkSegmentCount;
                bool walkable = CanFollowSegmentDense(previous, point, ignoreRoot,
                    out walkBlockedSegment, out walkSegmentCount);
                string compactWalkDetail;
                walkable = walkable && IsCompactWalkSegmentSafe(previous, point,
                    out compactWalkDetail);
                float rise = point.y - previous.y;
                bool jump = i < route.JumpFlags.Count && route.JumpFlags[i];
                // Derived links may only bridge disconnected RAIN polygons on a staircase.
                // A link that is physically walkable must never force the follower to jump.
                jump = jump && !walkable;
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
            AutoBattleRouteCapabilities capabilities, Transform ignoreRoot,
            out bool partial, out string detail)
        {
            partial = false;
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
            int denseRejects = 0;
            int aswnavRejects = 0;
            int detourCount = 0;
            int detourExpanded = 0;
            int inferredJumps = 0;
            while (cursor < clean.Count)
            {
                int selected = -1;
                int furthest = Mathf.Min(clean.Count - 1, cursor + 6);
                for (int candidate = furthest; candidate >= cursor; candidate--)
                {
                    if (candidate > cursor && !RainShortcutFollowsRawCorridor(anchor, clean, cursor, candidate,
                        RainShortcutCorridorRadius)) continue;
                    string compactSegmentDetail;
                    if (!IsCompactWalkSegmentSafe(anchor, clean[candidate],
                        out compactSegmentDetail))
                    {
                        aswnavRejects++;
                        continue;
                    }
                    int blockedSegment;
                    int segmentCount;
                    if (!CanFollowSegmentDense(anchor, clean[candidate], ignoreRoot,
                        out blockedSegment, out segmentCount))
                    {
                        denseRejects++;
                        continue;
                    }
                    selected = candidate;
                    break;
                }
                if (selected < 0)
                {
                    Vector3 blockedPoint = clean[cursor];
                    Vector3 jumpDirection = blockedPoint - anchor;
                    jumpDirection.y = 0f;
                    float jumpHorizontal = jumpDirection.magnitude;
                    float rise = blockedPoint.y - anchor.y;
                    bool lowObstacle = ShouldJumpForwardObstacle(anchor, jumpDirection,
                        ignoreRoot);
                    bool implicitJump = capabilities != null && capabilities.AllowJump &&
                        jumpHorizontal <= 4.2f && (rise > 0.62f || lowObstacle) &&
                        TryJumpSegment(anchor, blockedPoint, capabilities, ignoreRoot);
                    if (implicitJump)
                    {
                        simplified.Add(blockedPoint);
                        inferredJumps++;
                        anchor = blockedPoint;
                        cursor++;
                        continue;
                    }

                    List<Vector3> detour;
                    int resumeIndex;
                    int expanded;
                    string detourDetail;
                    if (!TryBuildRainLocalDetour(anchor, clean, cursor, furthest, ignoreRoot,
                        out detour, out resumeIndex, out expanded, out detourDetail))
                    {
                        partial = simplified.Count > 0;
                        detail = "opt=rain_blocked raw=" + rawCount + " clean=" + clean.Count +
                                 " at=" + cursor + " prefix=" + simplified.Count +
                                 " partial=" + (partial ? "1" : "0") +
                                 " denseRejects=" + denseRejects + " " + detourDetail;
                        DumpRainPathFailure(from, clean, null, simplified, null, ignoreRoot,
                            cursor, furthest, -1, detail);
                        return simplified;
                    }
                    for (int i = 0; i < detour.Count; i++) simplified.Add(detour[i]);
                    detourCount++;
                    detourExpanded += expanded;
                    anchor = clean[resumeIndex];
                    cursor = resumeIndex + 1;
                    continue;
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
                      " aswnavRejects=" + aswnavRejects +
                      " denseRejects=" + denseRejects +
                      " detours=" + detourCount +
                      " detourExpanded=" + detourExpanded +
                      " inferredJumps=" + inferredJumps +
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
                    int blockedSegment;
                    int segmentCount;
                    if (!CanFollowSegmentDense(previous, candidate, ignoreRoot,
                            out blockedSegment, out segmentCount) ||
                        !CanFollowSegmentDense(candidate, next, ignoreRoot,
                            out blockedSegment, out segmentCount))
                        continue;
                    string compactDetail;
                    if (!IsCompactWalkSegmentSafe(previous, candidate, out compactDetail) ||
                        !IsCompactWalkSegmentSafe(candidate, next, out compactDetail))
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
            if (!IsPlausibleWalkTransition(from, to)) return false;
            if (HasWalkSegment(from, to, ignoreRoot) && HasGroundSupportSegment(from, to, ignoreRoot))
                return true;
            return IsContinuousWalkableRamp(from, to, ignoreRoot);
        }

        public static bool IsDegenerateVerticalTransition(Vector3 from, Vector3 to)
        {
            return XZDistance(from, to) < SamePositionTolerance &&
                   Mathf.Abs(to.y - from.y) > SamePositionVerticalTolerance;
        }

        private static bool IsPlausibleWalkTransition(Vector3 from, Vector3 to)
        {
            float horizontal = XZDistance(from, to);
            float vertical = Mathf.Abs(to.y - from.y);
            if (horizontal < SamePositionTolerance)
                return vertical <= SamePositionVerticalTolerance;
            return vertical <= horizontal * MaxWalkableRampGrade + WalkGradeSlack;
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
            int aswnavRejects = 0;
            for (int i = 0; i < angles.Length; i++)
            {
                Vector3 candidateDirection = Quaternion.Euler(0f, angles[i], 0f) * desiredDirection;
                Vector3 raw = from + candidateDirection * 1.65f;
                Vector3 grounded;
                if (!TrySnapToGroundNear(raw, from.y, 1.35f, out grounded, false)) continue;
                if (!IsPointOnOwnedRainGraph(grounded, 1.0f)) continue;
                string compactDetail;
                if (!IsCompactWalkSegmentSafe(from, grounded, out compactDetail))
                {
                    aswnavRejects++;
                    continue;
                }
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
                     " score=" + bestScore.ToString("0.00") +
                     " aswnavRejects=" + aswnavRejects;
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

        public static bool HasForwardBlockToWaypoint(Vector3 from, Vector3 waypoint,
            Transform ignoreRoot)
        {
            Vector3 flat = waypoint - from;
            flat.y = 0f;
            float distance = flat.magnitude;
            if (distance < 0.08f) return false;

            Vector3 probeEnd = from + flat / distance * Mathf.Min(1.05f, distance);
            probeEnd.y = Mathf.Lerp(from.y, waypoint.y, Mathf.Min(1f, 1.05f / distance));
            return !CanTraverseWalkableSurface(from, probeEnd, ignoreRoot) ||
                   !HasNavigationBodyClearance(from, probeEnd, ignoreRoot,
                       NavigationBodyRadius, false);
        }

        public static bool CanFollowSegment(Vector3 from, Vector3 to, Transform ignoreRoot)
        {
            return CanTraverseWalkableSurface(from, to, ignoreRoot) &&
                   HasNavigationBodyClearance(from, to, ignoreRoot, NavigationBodyRadius);
        }

        public static bool CanFollowRouteSegment(Vector3 from, Vector3 to, Transform ignoreRoot)
        {
            int blockedSegment;
            int segmentCount;
            return CanFollowSegmentDense(from, to, ignoreRoot,
                out blockedSegment, out segmentCount);
        }

        public static string DescribeRouteSegment(Vector3 from, Vector3 to, Transform ignoreRoot)
        {
            int blockedSegment;
            int segmentCount;
            bool dense = CanFollowSegmentDense(from, to, ignoreRoot,
                out blockedSegment, out segmentCount);
            return "dense=" + (dense ? "1" : "0") +
                   " blocked=" + blockedSegment + "/" + segmentCount +
                   " shortBlock=" + (HasForwardBlockToWaypoint(from, to, ignoreRoot) ? "1" : "0") +
                   " graphTo=" + (IsPointOnOwnedRainGraph(to, 0.72f) ? "1" : "0") +
                   " standingTo=" + (HasStandingSpace(to, ignoreRoot) ? "1" : "0") +
                   " clearanceTo=" + MeasureWallClearance(to, ignoreRoot).ToString("0.00");
        }

        public static bool CanAdvanceToWaypoint(Vector3 from, Vector3 waypoint, bool jump,
            AutoBattleRouteCapabilities capabilities, Transform ignoreRoot)
        {
            if (!IsFinite(from) || !IsFinite(waypoint)) return false;
            string compactDetail;
            if (!jump && !IsCompactWalkSegmentSafe(from, waypoint, out compactDetail))
                return false;
            return jump
                ? CanExecuteJump(from, waypoint,
                    capabilities ?? new AutoBattleRouteCapabilities(), ignoreRoot)
                : CanFollowRouteSegment(from, waypoint, ignoreRoot);
        }

        public static int CopyPathForFollower(List<Vector3> points, List<bool> jumpFlags,
            Vector3 from, AutoBattleRouteCapabilities capabilities, Transform ignoreRoot,
            List<Vector3> outputPath, List<bool> outputJumps)
        {
            if (points == null || outputPath == null || outputJumps == null) return 0;
            Vector3 edgeStart = from;
            for (int i = 0; i < points.Count; i++)
            {
                if (!IsFinite(points[i])) return 0;
                if (IsDegenerateVerticalTransition(edgeStart, points[i])) return 0;
                bool jump = jumpFlags != null && i < jumpFlags.Count && jumpFlags[i];
                if (!CanAdvanceToWaypoint(edgeStart, points[i], jump, capabilities,
                    ignoreRoot))
                    return 0;
                edgeStart = points[i];
            }

            int startIndex = 0;
            while (startIndex < points.Count)
            {
                bool currentJump = jumpFlags != null && startIndex < jumpFlags.Count &&
                                   jumpFlags[startIndex];
                float horizontal = XZDistance(from, points[startIndex]);
                float heightError = Mathf.Abs(from.y - points[startIndex].y);
                if (horizontal >= 0.50f || heightError > (currentJump ? 0.35f : 1.25f)) break;
                startIndex++;
            }

            for (int i = startIndex; i < points.Count && outputPath.Count < 48; i++)
            {
                outputPath.Add(points[i]);
                outputJumps.Add(jumpFlags != null && i < jumpFlags.Count && jumpFlags[i]);
            }
            return startIndex;
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

        public static bool HasSupportedStandingPoint(Vector3 point, Transform ignoreRoot)
        {
            Vector3 grounded;
            return TrySnapToGroundNear(point, point.y, 0.72f, out grounded, false) &&
                   XZDistance(point, grounded) <= 0.20f &&
                   Mathf.Abs(point.y - grounded.y) <= 0.72f &&
                   HasStandingSpace(grounded, ignoreRoot);
        }

        public static bool HasWalkSegment(Vector3 from, Vector3 to, Transform ignoreRoot)
        {
            Vector3 a = from;
            Vector3 b = to;
            if (Mathf.Abs(a.y - b.y) > MaxStepHeight) return false;
            if (!IsPlausibleWalkTransition(a, b)) return false;
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
            Transform ignoreRoot,
            out List<Vector3> result, out string detail, out bool pending, out bool partial, out bool offMesh,
            out List<bool> offMeshFlags)
        {
            result = null;
            detail = "rain=not_tried";
            pending = false;
            partial = false;
            offMesh = false;
            offMeshFlags = null;
            if (_compactNavigationRequested)
            {
                List<bool> compactFlags;
                bool compactPending;
                bool compactOffMesh;
                bool compactResult = CompactRainNavRuntime.TryBuildPath(from, to, capabilities,
                    out result, out compactFlags, out compactPending, out compactOffMesh, out detail);
                pending = compactPending;
                offMesh = compactOffMesh;
                offMeshFlags = compactFlags;
                return compactResult;
            }
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
                    Vector3 pathFrom;
                    NavigationGraphNode startNode;
                    string startDetail;
                    if (!TryResolveRainStart(graph, from, to, ignoreRoot, null,
                        out pathFrom, out startNode, out startDetail))
                    {
                        _rainSearchJob = null;
                        detail = "rain=start_layer_unresolved " + startDetail;
                        return false;
                    }

                    RAINPathFinder finder = graph.CreatePathFinder();
                    if (finder == null)
                    {
                        _rainSearchJob = null;
                        detail = "rain=no_finder graph=" + graph.GetType().Name;
                        return false;
                    }

                    finder.MaxYOffset = 4f;
                    finder.MaxPathfindingSteps = RainPathStepsPerSlice;
                    finder.MaxPathLength = 1200f;
                    finder.StartPath(graph, pathFrom, to);
                    job = new RainSearchJob(graph, finder, from, to, pathFrom, startNode, startDetail);
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
                    if (job.Slices >= 120 ||
                        (job.Slices >= 8 && job.CpuMilliseconds >= 2500L))
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

                string endpointDetail = AnchorRainPathEndpoints(path, job.PathFrom, to);

                List<Vector3> linkedPath;
                List<bool> linkedFlags;
                offMesh = RuntimeRainNavDerivedData.TryBuildLinkedWorldPath(path, job.PathFrom, to,
                    out linkedPath, out linkedFlags);
                if (offMesh)
                {
                    result = linkedPath;
                    offMeshFlags = linkedFlags;
                    endpointDetail += " linked=poly_corridor";
                }
                else
                {
                    result = new List<Vector3>(path.WaypointCount);
                    for (int i = 0; i < path.WaypointCount; i++)
                        result.Add(path.GetWaypointPosition(i));
                }

                if (XZDistance(job.PathFrom, from) > 0.10f ||
                    Mathf.Abs(job.PathFrom.y - from.y) > 0.18f)
                {
                    result.Insert(0, from);
                    if (offMeshFlags != null) offMeshFlags.Insert(0, false);
                }

                Vector3 badStartPoint;
                if (TryGetInvalidRainStartTransition(from, result, offMeshFlags, out badStartPoint))
                {
                    Vector3 escape;
                    NavigationGraphNode escapeNode;
                    string escapeDetail;
                    NavigationGraphNode excluded = path.PathNodes != null && path.PathNodes.Count > 0
                        ? path.PathNodes[0]
                        : job.StartNode;
                    if (TryResolveRainStart(graph, from, to, ignoreRoot, excluded,
                        out escape, out escapeNode, out escapeDetail) &&
                        (XZDistance(escape, from) > 0.35f || Mathf.Abs(escape.y - from.y) > 0.30f))
                    {
                        result = new List<Vector3> { from, escape };
                        offMesh = false;
                        offMeshFlags = null;
                        partial = false;
                        detail = "rain=start_layer_escape bad=" + FormatVector(badStartPoint) +
                            " escape=" + FormatVector(escape) + " " + escapeDetail;
                        return true;
                    }

                    detail = "rain=start_layer_mismatch bad=" + FormatVector(badStartPoint) +
                        " " + escapeDetail;
                    result = null;
                    offMesh = false;
                    offMeshFlags = null;
                    return false;
                }
                partial = path.IsPartial;
                detail = "rain=ok graph=" + graph.GetType().Name + " pts=" + result.Count +
                    " partial=" + (path.IsPartial ? "1" : "0") + " offmesh=" + (offMesh ? "1" : "0") +
                    " slices=" + job.Slices + " ms=" + job.CpuMilliseconds + " " +
                    job.StartDetail + " " + endpointDetail;
                return result.Count > 0;
            }
            catch (Exception ex)
            {
                _rainSearchJob = null;
                detail = "rain=ex:" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 96);
                return false;
            }
        }

        private static bool TryResolveRainStart(RAINNavigationGraph graph, Vector3 from, Vector3 to,
            Transform ignoreRoot, NavigationGraphNode excludedNode, out Vector3 pathFrom,
            out NavigationGraphNode startNode, out string detail)
        {
            pathFrom = from;
            startNode = null;
            detail = "start=unresolved";
            if (graph == null) return false;

            NavigationGraphNode direct = null;
            Vector3 directSurface;
            try { direct = graph.QuantizeToNode(from, 4f); }
            catch { }
            if (direct != null && direct != excludedNode &&
                TryGetRainSurfacePoint(graph, direct, from, out directSurface) &&
                Mathf.Abs(directSurface.y - from.y) <= RainStartLayerTolerance)
            {
                startNode = direct;
                detail = "start=direct nodeType=" + direct.GetType().Name +
                    " surfaceY=" + directSurface.y.ToString("0.00") +
                    " dy=" + Mathf.Abs(directSurface.y - from.y).ToString("0.00");
                return true;
            }

            Vector3 metadataAnchor;
            NavigationGraphNode metadataNode;
            string metadataDetail;
            if (RuntimeRainNavDerivedData.TryFindSameLayerAnchor(graph, from, to, ignoreRoot,
                excludedNode, out metadataAnchor, out metadataNode, out metadataDetail))
            {
                pathFrom = metadataAnchor;
                startNode = metadataNode;
                detail = "start=relocated " + metadataDetail;
                return true;
            }

            Vector3 forward = to - from;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 right = new Vector3(forward.z, 0f, -forward.x);
            float bestScore = float.MaxValue;
            Vector3 best = Vector3.zero;
            NavigationGraphNode bestNode = null;
            float bestDy = 0f;
            float bestRadius = 0f;
            int tested = 0;
            int sameLayer = 0;
            int walkable = 0;
            int rings = Mathf.CeilToInt(RainStartAnchorMaxRadius / 0.65f);
            for (int ring = 1; ring <= rings; ring++)
            {
                float radius = Mathf.Min(RainStartAnchorMaxRadius, ring * 0.65f);
                int directions = ring <= 2 ? 12 : 20;
                for (int i = 0; i < directions; i++)
                {
                    float angle = (Mathf.PI * 2f * i) / directions;
                    Vector3 direction = forward * Mathf.Cos(angle) + right * Mathf.Sin(angle);
                    Vector3 probe = from + direction * radius;
                    NavigationGraphNode candidateNode;
                    try { candidateNode = graph.QuantizeToNode(probe, 1.35f); }
                    catch { continue; }
                    tested++;
                    if (candidateNode == null || candidateNode == excludedNode) continue;
                    Vector3 surface;
                    if (!TryGetRainSurfacePoint(graph, candidateNode, probe, out surface)) continue;
                    float dy = Mathf.Abs(surface.y - from.y);
                    if (dy > RainStartLayerTolerance) continue;
                    sameLayer++;
                    if (!HasStandingSpace(surface, ignoreRoot)) continue;
                    int blockedSegment;
                    int segmentCount;
                    if (!CanFollowSegmentDense(from, surface, ignoreRoot,
                        out blockedSegment, out segmentCount)) continue;
                    if (MeasureWallClearance(surface, ignoreRoot) < NavigationBodyRadius + 0.06f) continue;
                    walkable++;
                    float towardPenalty = Mathf.Max(0f, -Vector3.Dot(direction, forward)) * 0.45f;
                    float score = radius + dy * 1.6f + towardPenalty -
                        Mathf.Min(1.2f, MeasureWallClearance(surface, ignoreRoot)) * 0.20f;
                    if (score >= bestScore) continue;
                    bestScore = score;
                    best = surface;
                    bestNode = candidateNode;
                    bestDy = dy;
                    bestRadius = radius;
                }
                if (bestNode != null && ring >= 2) break;
            }

            if (bestNode == null)
            {
                string directText = direct == null ? "none" : direct.GetType().Name;
                float directDy = TryGetRainSurfacePoint(graph, direct, from, out directSurface)
                    ? Mathf.Abs(directSurface.y - from.y)
                    : -1f;
                detail = "start=anchor_failed direct=" + directText +
                    " directDy=" + directDy.ToString("0.00") +
                    " tested=" + tested + " sameLayer=" + sameLayer + " walkable=" + walkable +
                    " " + metadataDetail;
                return false;
            }

            pathFrom = best;
            startNode = bestNode;
            detail = "start=relocated nodeType=" + bestNode.GetType().Name +
                " radius=" + bestRadius.ToString("0.00") +
                " dy=" + bestDy.ToString("0.00") +
                " tested=" + tested + " sameLayer=" + sameLayer + " walkable=" + walkable;
            return true;
        }

        private static bool TryGetRainSurfacePoint(RAINNavigationGraph graph,
            NavigationGraphNode node, Vector3 worldProbe, out Vector3 worldSurface)
        {
            worldSurface = worldProbe;
            NavMeshPoly poly = node as NavMeshPoly;
            if (graph == null || poly == null) return false;
            try
            {
                Vector3 localProbe = graph.MountInverseTransform.MultiplyPoint(worldProbe);
                Vector3 localSurface;
                if (!poly.GetYInterceptPoint(localProbe, out localSurface)) return false;
                worldSurface = graph.MountTransform.MultiplyPoint(localSurface);
                return IsFinite(worldSurface);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetInvalidRainStartTransition(Vector3 from, List<Vector3> points,
            List<bool> jumpFlags, out Vector3 badPoint)
        {
            badPoint = Vector3.zero;
            if (points == null || points.Count < 2) return false;
            Vector3 previous = from;
            int inspected = 0;
            for (int i = 0; i < points.Count && inspected < 4; i++)
            {
                Vector3 candidate = points[i];
                if (XZDistance(previous, candidate) <= 0.18f &&
                    Mathf.Abs(candidate.y - previous.y) <= 0.30f)
                    continue;
                inspected++;
                bool linkedJump = jumpFlags != null && i < jumpFlags.Count && jumpFlags[i];
                float horizontal = XZDistance(previous, candidate);
                float vertical = Mathf.Abs(candidate.y - previous.y);
                if (!linkedJump && horizontal <= 1.35f && vertical > 2.40f)
                {
                    badPoint = candidate;
                    return true;
                }
                previous = candidate;
            }
            return false;
        }

        private static string AnchorRainPathEndpoints(RAINPath path, Vector3 from, Vector3 to)
        {
            try
            {
                int pathPointCount = path == null || path.PathPoints == null ? 0 : path.PathPoints.Count;
                if (pathPointCount < 2) return "endpoints=unavailable";

                // RAIN's path finder stores the start/goal polygon centers in PathPoints.
                // Replace them before NavMeshPath smoothing so the corridor starts and ends
                // at the requested world positions instead of repeatedly routing to centers.
                path.SetPathNode(0, from);
                if (!path.IsPartial) path.SetPathNode(pathPointCount - 1, to);

                float startError = path.WaypointCount > 0
                    ? XZDistance(path.GetWaypointPosition(0), from)
                    : float.MaxValue;
                float endError = !path.IsPartial && path.WaypointCount > 0
                    ? XZDistance(path.GetWaypointPosition(path.WaypointCount - 1), to)
                    : -1f;
                return "endpoints=" + (path.IsPartial ? "start_only" : "anchored") +
                       " startErr=" + startError.ToString("0.00") +
                       " endErr=" + endError.ToString("0.00");
            }
            catch (Exception ex)
            {
                return "endpoints=error:" + ex.GetType().Name;
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
                    float vertical = Mathf.Abs(target.y - previous.y);
                    if (vertical > SamePositionVerticalTolerance)
                    {
                        detail = "vertical_transition waypoint=" + i +
                                 " dy=" + vertical.ToString("0.00");
                        return false;
                    }
                    previous = target;
                    continue;
                }

                bool forcedJump = forcedJumpFlags != null && i < forcedJumpFlags.Count && forcedJumpFlags[i];
                if (forcedJump)
                {
                    int walkBlockedSegment;
                    int walkSegmentCount;
                    string compactWalkDetail;
                    if (IsCompactWalkSegmentSafe(previous, target, out compactWalkDetail) &&
                        CanFollowSegmentDense(previous, target, ignoreRoot,
                            out walkBlockedSegment, out walkSegmentCount))
                    {
                        previous = target;
                        continue;
                    }
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

                string compactSegmentDetail;
                if (!IsCompactWalkSegmentSafe(previous, target, out compactSegmentDetail))
                {
                    detail = "aswnav_unsafe waypoint=" + i + " " + compactSegmentDetail;
                    return false;
                }
                int blockedSegment;
                int segments;
                if (!CanFollowSegmentDense(previous, target, ignoreRoot, out blockedSegment, out segments))
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
                        detail = "blocked_walk waypoint=" + i + " segment=" +
                                 blockedSegment + "/" + segments;
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

        private static bool IsCompactWalkSegmentSafe(Vector3 from, Vector3 to,
            out string detail)
        {
            if (!_compactNavigationRequested)
            {
                detail = "aswnav=inactive";
                return true;
            }
            return CompactRainNavRuntime.IsSafeWalkSegment(from, to, out detail);
        }

        private static List<Vector3> OptimizeRainPathWithHardLinks(Vector3 from, List<Vector3> points,
            List<bool> jumpFlags, AutoBattleRouteCapabilities capabilities, Transform ignoreRoot,
            out List<bool> optimizedFlags,
            out bool partial, out string detail)
        {
            optimizedFlags = new List<bool>();
            partial = false;
            List<Vector3> optimized = new List<Vector3>();
            if (points == null || points.Count == 0)
            {
                detail = "opt=offmesh_empty";
                return optimized;
            }

            Vector3 anchor = from;
            int index = 0;
            int removed = 0;
            int detourCount = 0;
            int detourExpanded = 0;
            int inferredJumps = 0;
            int walkLinks = 0;
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
                    int chosen = -1;
                    int blockedSegment = 0;
                    int segmentCount = 0;
                    for (int candidate = normalEnd; candidate >= cursor; candidate--)
                    {
                        if (!CanFollowSegmentDense(anchor, points[candidate], ignoreRoot,
                            out blockedSegment, out segmentCount)) continue;
                        chosen = candidate;
                        break;
                    }
                    if (chosen < 0)
                    {
                        Vector3 blockedPoint = points[cursor];
                        Vector3 jumpDirection = blockedPoint - anchor;
                        jumpDirection.y = 0f;
                        float jumpHorizontal = jumpDirection.magnitude;
                        float rise = blockedPoint.y - anchor.y;
                        bool lowObstacle = ShouldJumpForwardObstacle(anchor, jumpDirection,
                            ignoreRoot);
                        bool implicitJump = capabilities != null && capabilities.AllowJump &&
                            jumpHorizontal <= 4.2f && (rise > 0.62f || lowObstacle) &&
                            TryJumpSegment(anchor, blockedPoint, capabilities, ignoreRoot);
                        if (implicitJump)
                        {
                            AddOptimizedPoint(optimized, optimizedFlags, blockedPoint, true);
                            inferredJumps++;
                            anchor = blockedPoint;
                            cursor++;
                            continue;
                        }

                        List<Vector3> detour;
                        int resumeIndex;
                        int expanded;
                        string detourDetail;
                        if (!TryBuildRainLocalDetour(anchor, points, cursor, normalEnd, ignoreRoot,
                            out detour, out resumeIndex, out expanded, out detourDetail))
                        {
                            partial = optimized.Count > 0;
                            detail = "opt=offmesh_blocked in=" + points.Count + " at=" + cursor +
                                     " normalEnd=" + normalEnd + " jumpIndex=" + jumpIndex +
                                     " anchor=" + FormatVector(anchor) +
                                     " blocked=" + FormatVector(points[cursor]) +
                                     " segment=" + blockedSegment + "/" + segmentCount +
                                     " implicitJump=0" +
                                     " prefix=" + optimized.Count + " partial=" + (partial ? "1" : "0") +
                                     " removed=" + removed + " " + detourDetail;
                            DumpRainPathFailure(from, points, jumpFlags, optimized, optimizedFlags,
                                ignoreRoot, cursor, normalEnd, jumpIndex, detail);
                            return optimized;
                        }
                        for (int i = 0; i < detour.Count; i++)
                            AddOptimizedPoint(optimized, optimizedFlags, detour[i], false);
                        detourCount++;
                        detourExpanded += expanded;
                        removed += resumeIndex - cursor;
                        anchor = points[resumeIndex];
                        cursor = resumeIndex + 1;
                        continue;
                    }
                    AddOptimizedPoint(optimized, optimizedFlags, points[chosen], false);
                    removed += chosen - cursor;
                    anchor = points[chosen];
                    cursor = chosen + 1;
                }
                if (jumpIndex < 0) break;
                int walkBlockedSegment;
                int walkSegmentCount;
                if (CanFollowSegmentDense(anchor, points[jumpIndex], ignoreRoot,
                    out walkBlockedSegment, out walkSegmentCount))
                {
                    AddOptimizedPoint(optimized, optimizedFlags, points[jumpIndex], false);
                    walkLinks++;
                }
                else
                {
                    List<Vector3> walkDetour;
                    int walkResumeIndex;
                    int walkExpanded;
                    string walkDetail;
                    if (TryBuildRainLocalDetour(anchor, points, jumpIndex, jumpIndex,
                        ignoreRoot, out walkDetour, out walkResumeIndex, out walkExpanded,
                        out walkDetail))
                    {
                        for (int i = 0; i < walkDetour.Count; i++)
                            AddOptimizedPoint(optimized, optimizedFlags, walkDetour[i], false);
                        detourCount++;
                        detourExpanded += walkExpanded;
                        walkLinks++;
                    }
                    else
                    {
                        AddOptimizedPoint(optimized, optimizedFlags, points[jumpIndex], true);
                    }
                }
                anchor = points[jumpIndex];
                index = jumpIndex + 1;
            }
            detail = "opt=offmesh_hard_anchors in=" + points.Count + " out=" + optimized.Count +
                " removed=" + removed + " detours=" + detourCount +
                " detourExpanded=" + detourExpanded + " inferredJumps=" + inferredJumps +
                " walkLinks=" + walkLinks;
            return optimized;
        }

        private static bool TryBuildRainLocalDetour(Vector3 from, List<Vector3> path,
            int first, int last, Transform ignoreRoot, out List<Vector3> detour,
            out int resumeIndex, out int expanded, out string detail)
        {
            detour = new List<Vector3>();
            resumeIndex = -1;
            expanded = 0;
            detail = "rainDetour=none";
            if (path == null || first < 0 || first >= path.Count || last < first) return false;
            last = Mathf.Min(last, path.Count - 1);

            List<GridNode> open = new List<GridNode>(256);
            Dictionary<GridKey, GridNode> nodes = new Dictionary<GridKey, GridNode>();
            GridNode start = new GridNode();
            start.X = 0;
            start.Z = 0;
            start.Layer = HeightLayer(from.y);
            start.Key = new GridKey(0, 0, start.Layer);
            start.Pos = from;
            start.G = 0f;
            start.H = RainDetourHeuristic(from, path, first, last);
            open.Add(start);
            nodes[start.Key] = start;

            GridNode connected = null;
            int reconnectRejectFollow = 0;
            int rejectBounds = 0;
            int rejectGround = 0;
            int rejectGraph = 0;
            int rejectStanding = 0;
            int rejectFollow = 0;
            int rejectClearance = 0;
            int rejectKnown = 0;
            float minimumY = from.y;
            float maximumY = from.y;
            while (open.Count > 0 && expanded < RainDetourMaxExpanded)
            {
                int bestIndex = PopBestIndex(open);
                GridNode current = open[bestIndex];
                open.RemoveAt(bestIndex);
                if (current.Closed) continue;
                current.Closed = true;
                expanded++;

                if (current != start)
                {
                    for (int candidate = last; candidate >= first; candidate--)
                    {
                        int blockedSegment;
                        int segmentCount;
                        if (!CanFollowSegmentDense(current.Pos, path[candidate], ignoreRoot,
                            out blockedSegment, out segmentCount))
                        {
                            reconnectRejectFollow++;
                            continue;
                        }
                        connected = current;
                        resumeIndex = candidate;
                        break;
                    }
                    if (connected != null) break;
                }

                for (int direction = 0; direction < Dx.Length; direction++)
                {
                    int nx = current.X + Dx[direction];
                    int nz = current.Z + Dz[direction];
                    if (Mathf.Abs(nx) > RainDetourRadiusCells || Mathf.Abs(nz) > RainDetourRadiusCells)
                    {
                        rejectBounds++;
                        continue;
                    }

                    Vector3 raw = new Vector3(from.x + nx * RainDetourCellSize,
                        current.Pos.y, from.z + nz * RainDetourCellSize);
                    Vector3 grounded;
                    if (!TrySnapToGroundNear(raw, current.Pos.y, 1.20f, out grounded, false))
                    {
                        rejectGround++;
                        continue;
                    }
                    minimumY = Mathf.Min(minimumY, grounded.y);
                    maximumY = Mathf.Max(maximumY, grounded.y);
                    if (!IsPointOnOwnedRainGraph(grounded, 0.72f))
                    {
                        rejectGraph++;
                        continue;
                    }
                    if (!HasStandingSpace(grounded, ignoreRoot))
                    {
                        rejectStanding++;
                        continue;
                    }
                    int blockedSegment;
                    int segmentCount;
                    if (!CanFollowSegmentDense(current.Pos, grounded, ignoreRoot,
                        out blockedSegment, out segmentCount))
                    {
                        rejectFollow++;
                        continue;
                    }
                    float clearance = MeasureWallClearance(grounded, ignoreRoot);
                    if (clearance < NavigationBodyRadius + 0.06f)
                    {
                        rejectClearance++;
                        continue;
                    }

                    GridKey key = new GridKey(nx, nz, HeightLayer(grounded.y));
                    float stepCost = XZDistance(current.Pos, grounded) +
                                     Mathf.Abs(grounded.y - current.Pos.y) * 0.65f +
                                     Mathf.Max(0f, 0.90f - clearance) * 0.8f;
                    float tentative = current.G + stepCost;
                    GridNode node;
                    if (nodes.TryGetValue(key, out node))
                    {
                        if (node.Closed || tentative >= node.G - 0.02f)
                        {
                            rejectKnown++;
                            continue;
                        }
                        node.G = tentative;
                        node.H = RainDetourHeuristic(grounded, path, first, last);
                        node.Pos = grounded;
                        node.Parent = current;
                        open.Add(node);
                        continue;
                    }

                    node = new GridNode();
                    node.X = nx;
                    node.Z = nz;
                    node.Layer = key.Layer;
                    node.Key = key;
                    node.Pos = grounded;
                    node.G = tentative;
                    node.H = RainDetourHeuristic(grounded, path, first, last);
                    node.Parent = current;
                    nodes[key] = node;
                    open.Add(node);
                }
            }

            if (connected == null || resumeIndex < first)
            {
                string termination = open.Count == 0 ? "open_exhausted" : "max_expanded";
                detail = "rainDetour=failed term=" + termination +
                         " expanded=" + expanded + " nodes=" + nodes.Count +
                         " reconnectReject=" + reconnectRejectFollow +
                         " rejectBounds=" + rejectBounds + " rejectGround=" + rejectGround +
                         " rejectGraph=" + rejectGraph + " rejectStanding=" + rejectStanding +
                         " rejectFollow=" + rejectFollow + " rejectClearance=" + rejectClearance +
                         " rejectKnown=" + rejectKnown +
                         " y=" + minimumY.ToString("0.00") + ".." + maximumY.ToString("0.00");
                return false;
            }

            List<Vector3> rawDetour = new List<Vector3>();
            GridNode cursor = connected;
            while (cursor != null && cursor != start)
            {
                rawDetour.Add(cursor.Pos);
                cursor = cursor.Parent;
            }
            rawDetour.Reverse();
            rawDetour.Add(path[resumeIndex]);

            Vector3 anchor = from;
            int rawIndex = 0;
            while (rawIndex < rawDetour.Count)
            {
                int chosen = rawIndex;
                for (int candidate = rawDetour.Count - 1; candidate > rawIndex; candidate--)
                {
                    int blockedSegment;
                    int segmentCount;
                    if (!CanFollowSegmentDense(anchor, rawDetour[candidate], ignoreRoot,
                        out blockedSegment, out segmentCount)) continue;
                    chosen = candidate;
                    break;
                }
                detour.Add(rawDetour[chosen]);
                anchor = rawDetour[chosen];
                rawIndex = chosen + 1;
            }

            detail = "rainDetour=ok expanded=" + expanded + " nodes=" + nodes.Count +
                     " points=" + detour.Count + " resume=" + resumeIndex;
            return detour.Count > 0;
        }

        private static void DumpRainPathFailure(Vector3 from, List<Vector3> rawPoints,
            List<bool> rawFlags, List<Vector3> prefix, List<bool> prefixFlags,
            Transform ignoreRoot, int failedIndex, int normalEnd, int jumpIndex, string reason)
        {
            try
            {
                Vector3 to = rawPoints != null && rawPoints.Count > 0
                    ? rawPoints[rawPoints.Count - 1]
                    : from;
                string signature = RuntimeRainNavMesh.CurrentMapName + "|" +
                    Mathf.RoundToInt(from.x * 2f) + ":" + Mathf.RoundToInt(from.y * 2f) + ":" + Mathf.RoundToInt(from.z * 2f) + "|" +
                    Mathf.RoundToInt(to.x * 2f) + ":" + Mathf.RoundToInt(to.y * 2f) + ":" + Mathf.RoundToInt(to.z * 2f) + "|" +
                    failedIndex + ":" + normalEnd + ":" + jumpIndex;
                if (RainFailureDumps.Contains(signature)) return;
                if (RainFailureDumps.Count >= 32) RainFailureDumps.Clear();
                RainFailureDumps.Add(signature);

                string directory = Path.Combine(Path.Combine(Application.persistentDataPath,
                    "ASWDEBUG"), "NavDiagnostics");
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, "rain_route_failure_pid" +
                    Process.GetCurrentProcess().Id + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff") + ".txt");
                StringBuilder builder = new StringBuilder(32768);
                builder.AppendLine("map=" + RuntimeRainNavMesh.CurrentMapName);
                builder.AppendLine("from=" + FormatVector(from) + " to=" + FormatVector(to));
                builder.AppendLine("failedIndex=" + failedIndex + " normalEnd=" + normalEnd +
                    " jumpIndex=" + jumpIndex);
                builder.AppendLine("reason=" + reason);
                AppendRainDumpPoints(builder, "raw", from, rawPoints, rawFlags, ignoreRoot);
                AppendRainDumpPoints(builder, "accepted_prefix", from, prefix, prefixFlags, ignoreRoot);
                File.WriteAllText(file, builder.ToString(), Encoding.UTF8);
                FileLogger.Log("AUTO-BATTLE][ROUTE-DUMP", "saved=" + file +
                    " raw=" + (rawPoints == null ? 0 : rawPoints.Count) +
                    " prefix=" + (prefix == null ? 0 : prefix.Count));
            }
            catch (Exception ex)
            {
                FileLogger.Log("AUTO-BATTLE][ROUTE-DUMP", "failed=" + ex.GetType().Name + ":" +
                    SafeOneLine(ex.Message, 120));
            }
        }

        public static void DumpFollowerPathFailure(Vector3 from, List<Vector3> points,
            List<bool> flags, int startIndex, Transform ignoreRoot, string reason)
        {
            try
            {
                int count = points == null ? 0 : points.Count;
                startIndex = Mathf.Clamp(startIndex, 0, count);
                Vector3 to = count > 0 ? points[count - 1] : from;
                string signature = "follow|" + RuntimeRainNavMesh.CurrentMapName + "|" +
                    Mathf.RoundToInt(from.x * 2f) + ":" + Mathf.RoundToInt(from.y * 2f) + ":" + Mathf.RoundToInt(from.z * 2f) + "|" +
                    Mathf.RoundToInt(to.x * 2f) + ":" + Mathf.RoundToInt(to.y * 2f) + ":" + Mathf.RoundToInt(to.z * 2f) + "|" +
                    startIndex + "|" + SafeOneLine(reason, 64);
                if (RainFailureDumps.Contains(signature)) return;
                if (RainFailureDumps.Count >= 32) RainFailureDumps.Clear();
                RainFailureDumps.Add(signature);

                List<Vector3> remaining = new List<Vector3>(Mathf.Min(48, count - startIndex));
                List<bool> remainingFlags = new List<bool>(Mathf.Min(48, count - startIndex));
                for (int i = startIndex; i < count && remaining.Count < 48; i++)
                {
                    remaining.Add(points[i]);
                    remainingFlags.Add(flags != null && i < flags.Count && flags[i]);
                }

                string directory = Path.Combine(Path.Combine(Application.persistentDataPath,
                    "ASWDEBUG"), "NavDiagnostics");
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, "rain_follow_failure_pid" +
                    Process.GetCurrentProcess().Id + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff") + ".txt");
                StringBuilder builder = new StringBuilder(16384);
                builder.AppendLine("map=" + RuntimeRainNavMesh.CurrentMapName);
                builder.AppendLine("from=" + FormatVector(from) + " to=" + FormatVector(to));
                builder.AppendLine("startIndex=" + startIndex + " total=" + count);
                builder.AppendLine("reason=" + reason);
                if (remaining.Count > 0)
                    builder.AppendLine("first=" + DescribeRouteSegment(from, remaining[0], ignoreRoot));
                AppendRainDumpPoints(builder, "follower_remaining", from, remaining,
                    remainingFlags, ignoreRoot);
                File.WriteAllText(file, builder.ToString(), Encoding.UTF8);
                FileLogger.Log("AUTO-BATTLE][ROUTE-DUMP", "saved=" + file +
                    " remaining=" + remaining.Count + " reason=" + SafeOneLine(reason, 96));
            }
            catch (Exception ex)
            {
                FileLogger.Log("AUTO-BATTLE][ROUTE-DUMP", "failed=" + ex.GetType().Name + ":" +
                    SafeOneLine(ex.Message, 120));
            }
        }

        private static void AppendRainDumpPoints(StringBuilder builder, string label, Vector3 from,
            List<Vector3> points, List<bool> flags, Transform ignoreRoot)
        {
            int count = points == null ? 0 : points.Count;
            builder.AppendLine("[" + label + "] count=" + count);
            Vector3 previous = from;
            for (int i = 0; i < count; i++)
            {
                Vector3 point = points[i];
                int blockedSegment;
                int segmentCount;
                bool follow = CanFollowSegmentDense(previous, point, ignoreRoot,
                    out blockedSegment, out segmentCount);
                bool jump = flags != null && i < flags.Count && flags[i];
                builder.Append(i.ToString("D3")).Append(" point=").Append(FormatVector(point))
                    .Append(" from=").Append(FormatVector(previous))
                    .Append(" dist=").Append(XZDistance(previous, point).ToString("0.00"))
                    .Append(" dy=").Append((point.y - previous.y).ToString("0.00"))
                    .Append(" jump=").Append(jump ? "1" : "0")
                    .Append(" follow=").Append(follow ? "1" : "0")
                    .Append(" blocked=").Append(blockedSegment).Append('/').Append(segmentCount)
                    .Append(" graph=").Append(IsPointOnOwnedRainGraph(point, 0.72f) ? "1" : "0")
                    .Append(" standing=").Append(HasStandingSpace(point, ignoreRoot) ? "1" : "0")
                    .Append(" clearance=").Append(MeasureWallClearance(point, ignoreRoot).ToString("0.00"))
                    .AppendLine();
                previous = point;
            }
        }

        private static string FormatVector(Vector3 value)
        {
            return value.x.ToString("0.00") + "," + value.y.ToString("0.00") + "," +
                   value.z.ToString("0.00");
        }

        private static float RainDetourHeuristic(Vector3 point, List<Vector3> path, int first, int last)
        {
            float best = float.MaxValue;
            for (int i = first; i <= last; i++)
            {
                float distance = XZDistance(point, path[i]) + Mathf.Abs(point.y - path[i].y) * 1.35f;
                if (distance < best) best = distance;
            }
            return best;
        }

        private static bool CanFollowSegmentDense(Vector3 from, Vector3 to, Transform ignoreRoot,
            out int blockedSegment, out int segmentCount)
        {
            float horizontal = XZDistance(from, to);
            segmentCount = Mathf.Clamp(Mathf.CeilToInt(horizontal / 0.9f), 1, 96);
            blockedSegment = 0;
            if (!IsPlausibleWalkTransition(from, to))
            {
                blockedSegment = 1;
                return false;
            }
            Vector3 segmentStart = from;
            for (int segment = 1; segment <= segmentCount; segment++)
            {
                Vector3 segmentEnd = Vector3.Lerp(from, to, (float)segment / segmentCount);
                if (!CanFollowSegment(segmentStart, segmentEnd, ignoreRoot))
                {
                    blockedSegment = segment;
                    return false;
                }
                segmentStart = segmentEnd;
            }
            return true;
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
            if (_compactNavigationRequested)
            {
                CompactRainRuntimeSnapshot compact = CompactRainNavRuntime.GetSnapshot();
                if (compact.Failed)
                {
                    SetNavigationState(AutoBattleNavResourceState.Fallback,
                        "map=" + SafeMap(_navMapName) + " provider=aswnav_0_10 reason=" +
                        CompactRainNavRuntime.Detail);
                    return;
                }
                if (compact.Ready)
                {
                    bool projected = CompactRainNavRuntime.IsPointOnGraph(probe, 1.25f);
                    SetNavigationState(AutoBattleNavResourceState.Ready,
                        "map=" + SafeMap(_navMapName) + " provider=aswnav_0_10" +
                        " scene=" + compact.SceneEpoch + " probe=" + (projected ? "1" : "0") +
                        " fileBytes=" + compact.FileBytes + " resident=" + compact.ResidentBytes +
                        " workspace=" + compact.WorkspaceBytes + " polys=" + compact.PolyCount +
                        " portals=" + compact.PortalCount + " loads=" + compact.DatasetLoadCount +
                        " activeQueries=" + compact.ActiveQueryCount);
                    return;
                }
                SetNavigationState(AutoBattleNavResourceState.Loading,
                    "map=" + SafeMap(_navMapName) + " provider=aswnav_0_10 " +
                    CompactRainNavRuntime.Detail);
                return;
            }
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
            if (!HasSupportedStandingPoint(from, ignoreRoot) ||
                !HasSupportedStandingPoint(to, ignoreRoot)) return false;

            Vector3 previous = from;
            float arc = Mathf.Max(0.72f, jumpHeight - Mathf.Max(0f, rise) * 0.35f);
            const int samples = 12;
            for (int i = 1; i <= samples; i++)
            {
                float t = (float)i / samples;
                Vector3 next = Vector3.Lerp(from, to, t);
                next.y += 4.0f * arc * t * (1.0f - t);
                Vector3 forward = next - previous;
                forward.y = 0f;
                Vector3 side = forward.sqrMagnitude > 0.001f
                    ? Vector3.Cross(Vector3.up, forward.normalized)
                    : Vector3.right;
                for (int s = 0; s < JumpProbeSideOffsets.Length; s++)
                {
                    for (int h = 0; h < JumpProbeHeights.Length; h++)
                    {
                        Vector3 offset = side * JumpProbeSideOffsets[s] +
                                         Vector3.up * JumpProbeHeights[h];
                        Vector3 probeStart = previous + offset;
                        Vector3 probeEnd = next + offset;
                        Vector3 segment = probeEnd - probeStart;
                        float length = segment.magnitude;
                        if (length <= 0.01f) continue;
                        RaycastHit[] hits = Physics.RaycastAll(probeStart, segment / length,
                            length, BlockMask);
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
            public readonly Vector3 PathFrom;
            public readonly NavigationGraphNode StartNode;
            public readonly string StartDetail;
            public int Slices;
            public long CpuMilliseconds;

            public RainSearchJob(RAINNavigationGraph graph, RAINPathFinder finder, Vector3 from, Vector3 to,
                Vector3 pathFrom, NavigationGraphNode startNode, string startDetail)
            {
                Graph = graph;
                Finder = finder;
                From = from;
                To = to;
                PathFrom = pathFrom;
                StartNode = startNode;
                StartDetail = startDetail ?? "start=unknown";
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
